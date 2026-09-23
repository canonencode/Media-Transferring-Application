/* The page's half of the bridge.
 *
 * Two things only the host process can do: start the scanner, and read the
 * database it writes. Everything else happens here. Messages are plain JSON in
 * both directions - no host objects are exposed to the page, so a bug in this
 * file cannot reach the filesystem.
 *
 * Page to host:  { cmd: "load" }  |  { cmd: "scan" }
 * Host to page:  data | empty | error | scanStarted | progress | scanEnded
 */
(function () {
  "use strict";

  var host = window.chrome && window.chrome.webview;
  var D = null;

  // A script error here leaves the window showing whatever it last drew, with
  // no console anyone will open. Reporting it to the host puts it in the log
  // next to the database, which is the only place a frozen screen can be
  // explained after the fact.
  window.addEventListener("error", function (ev) {
    if (host) {
      host.postMessage({
        cmd: "log",
        text: "sayfa hatasi: " + (ev.message || "") + " @ " +
              (ev.filename || "") + ":" + (ev.lineno || 0)
      });
    }
  });
  window.addEventListener("unhandledrejection", function (ev) {
    if (host) host.postMessage({ cmd: "log", text: "sayfa reddi: " + ev.reason });
  });

  var COLS = [
    { key: "n", label: "Ad",    dir: 1,  cls: "" },
    { key: "t", label: "Tür",   dir: 1,  cls: "r c-type" },
    { key: "s", label: "Boyut", dir: -1, cls: "r" },
    { key: "d", label: "Tarih", dir: -1, cls: "r c-date" }
  ];
  var MAX = 500;
  var state = { tab: "all", sort: "d", dir: -1, scanning: false };

  var strip = document.getElementById("strip");
  var scanbar = document.getElementById("scanbar");
  var scanfill = document.getElementById("scanfill");
  var tabs = document.getElementById("tabs");
  var content = document.getElementById("content");
  var statusbar = document.getElementById("statusbar");
  var rescan = document.getElementById("rescan");
  var transferBtn = document.getElementById("transfer");
  var setup = document.getElementById("setup");

  /* ---------------- helpers ---------------- */

  function fmtInt(n) { return (n || 0).toLocaleString("tr-TR"); }
  function fmtBytes(b) {
    if (!b) return "";
    var u = ["B", "KB", "MB", "GB"], i = 0, v = b;
    while (v >= 1024 && i < u.length - 1) { v /= 1024; i++; }
    return (v >= 100 || i === 0 ? Math.round(v) : v.toFixed(1)) + " " + u[i];
  }
  function el(tag, cls, text) {
    var e = document.createElement(tag);
    if (cls) e.className = cls;
    if (text != null) e.textContent = text;
    return e;
  }
  function ext(name) {
    var i = name.lastIndexOf(".");
    return i < 0 ? "" : name.slice(i + 1).toLowerCase();
  }
  var PHOTO = { jpg:1, jpeg:1, png:1, heic:1, heif:1, webp:1, gif:1, bmp:1, tif:1, tiff:1, dng:1, jxl:1 };
  var VIDEO = { mp4:1, mov:1, mkv:1, webm:1, "3gp":1, avi:1, m4v:1 };
  function tur(f) {
    if (f.k === "Document") return "Belge";
    var e = ext(f.n);
    if (PHOTO[e]) return "Fotoğraf";
    if (VIDEO[e]) return "Video";
    return "Dosya";
  }
  function fmtDate(d) { return (d || "").replace(/[/-]/g, "."); }

  function setStrip(kind, bold, rest) {
    strip.hidden = false;
    strip.className = "strip" + (kind ? " " + kind : "");
    strip.innerHTML = "";
    if (bold) strip.appendChild(el("b", null, bold));
    if (rest) strip.appendChild(el("span", null, rest));
  }

  /* ---------------- host messages ---------------- */

  function send(msg) {
    if (host) host.postMessage(msg);
  }

  if (host) {
    host.addEventListener("message", function (e) {
      var m = e.data;
      if (!m || !m.type) return;

      if (m.type === "data") { D = m.payload; boot(); }
      else if (m.type === "empty") { showNotice(m.message, true); }
      else if (m.type === "error") { showNotice(m.message, false); }
      else if (m.type === "scanStarted") {
        state.scanning = true;
        openLive();
        rescan.disabled = true;
        transferBtn.disabled = true;
        closeSetup();
        scanbar.hidden = false;
        scanbar.className = "scanbar unknown";
        setStrip("info", "Taranıyor.", "Telefon okunuyor.");
      }
      else if (m.type === "progress") {
        if (state.scanning) showProgress(m);
      }
      else if (m.type === "drives") { renderSetup(m); }
      else if (m.type === "browsed") { setupState.root = m.path; setupState.custom = true; drawSetup(); askPreflight(); }
      else if (m.type === "preflight") { showPreflight(m); }
      else if (m.type === "progressLost") {
        setStrip("", "İlerleme okunamıyor.", m.message);
      }
      else if (m.type === "scanEnded") {
        state.scanning = false;
        liveRows = null;
        rescan.disabled = false;
        transferBtn.disabled = false;
        scanbar.hidden = true;
        if (m.crashed) setStrip("", "Tarama yarıda kaldı.", m.message);
      }
    });
  }

  rescan.addEventListener("click", function () {
    if (state.scanning) return;
    if (!host) {
      setStrip("", "Tarama bu sayfada çalışmaz.",
        "Telefonu USB üzerinden okumak masaüstü uygulamasının işi.");
      return;
    }
    send({ cmd: "scan" });
  });

  function fmtClock(sec) {
    var m = Math.floor(sec / 60), s = sec % 60;
    return m ? m + " dk " + s + " sn" : s + " sn";
  }

  function showProgress(m) {
    var parts = [];
    if (m.expected && m.expected > 0 && m.files <= m.expected) {
      // "yaklasik", cunku bu gecen taramanin sayisi ve telefon o gunden beri
      // dosya kazanmis ya da kaybetmis olabilir. Olcek vermek, hic olcek
      // vermemekten iyi; kesinmis gibi gostermek ikisinden de kotu.
      parts.push(fmtInt(m.files) + " / yaklaşık " + fmtInt(m.expected) + " dosya");
      scanbar.className = "scanbar";
      scanfill.style.width = Math.min(100, 100 * m.files / m.expected).toFixed(1) + "%";
    } else {
      parts.push(fmtInt(m.files) + " dosya");
      scanbar.className = "scanbar unknown";
    }
    parts.push(fmtInt(m.folders) + " klasör");
    if (m.elapsed) parts.push(fmtClock(m.elapsed));
    if (m.folder) parts.push(m.folder);

    setStrip("info", "Taranıyor.", parts.join("  ·  "));

    if (m.arrived && m.arrived.length) addLive(m.arrived);
  }

  /* Bekleme sirasinda sayinin artmasini izlemek, calisip calismadigini
     soylemiyor. Dosyalarin kendisini izlemek soyluyor.

     Gelen satirlar mevcut listenin basina EKLENIYOR, liste her yarim saniyede
     bir bastan cizilmiyor. Bastan cizmek 300 satirlik bir DOM'u saniyede iki
     kez yikip kuruyordu ve goz bunu titreme olarak goruyor; ayrica kaydirma
     konumunu da her seferinde sifirliyordu. */

  var liveHead = null;
  var liveRows = null;

  function liveRow(f) {
    var r = el("div", "row");
    var nm = el("span", "nm");
    nm.appendChild(document.createTextNode(f.n));
    nm.appendChild(document.createTextNode("  "));
    nm.appendChild(el("span", "src", (D && D.labels && D.labels[f.src]) || ""));
    r.appendChild(nm);
    r.appendChild(el("span", "meta r c-type", tur(f)));
    r.appendChild(el("span", "meta r num", fmtBytes(f.s)));
    r.appendChild(el("span", "meta r num c-date", fmtDate(f.d)));
    return r;
  }

  function openLive() {
    content.innerHTML = "";
    liveHead = el("div", "live", "İlk dosyalar bekleniyor");
    liveRows = el("div");
    content.appendChild(liveHead);
    content.appendChild(liveRows);
  }

  function addLive(files) {
    if (!liveRows || !liveRows.isConnected) openLive();
    if (!files.length) return;

    liveHead.textContent = "Bulunanlar, en yenisi üstte";

    // Ustte duruyorsa yeni satirlar akip gelsin. Asagi kaydirmissa okudugu
    // yerde kalsin: ustten eklemek icerigi asagi iter ve satirlar elinin
    // altindan kayar.
    var pinned = content.scrollTop > 4;
    var before = liveRows.offsetHeight;

    var block = document.createDocumentFragment();
    files.forEach(function (f) { block.appendChild(liveRow(f)); });
    liveRows.insertBefore(block, liveRows.firstChild);

    while (liveRows.children.length > 300) {
      liveRows.removeChild(liveRows.lastChild);
    }
    if (pinned) content.scrollTop += liveRows.offsetHeight - before;
  }

  /* ---------------- aktarım kurulumu ---------------- */

  var setupState = { drives: [], root: null, folder: "", group: true, scanId: null, custom: false, pre: null };

  transferBtn.addEventListener("click", function () {
    if (state.scanning) return;
    if (!host) return;
    send({ cmd: "drives" });
  });

  function openSetup() { setup.hidden = false; content.hidden = true; }
  function closeSetup() { setup.hidden = true; content.hidden = false; }

  function renderSetup(m) {
    setupState.drives = m.drives || [];
    setupState.folder = m.defaultFolder || "Telefon yedek";
    setupState.scanId = m.scanId;
    setupState.custom = false;
    setupState.pre = null;

    // Once bos yeri en cok olan surucu secili gelsin: kullanicinin buraya
    // gelme sebebi 22 GB'lik bir kopyalama, ve C: cogu makinede en dolu disk.
    var best = null;
    setupState.drives.forEach(function (d) {
      if (d.ready && (!best || d.free > best.free)) best = d;
    });
    setupState.root = best ? best.root : null;

    openSetup();
    drawSetup();
    askPreflight();
  }

  function askPreflight() {
    if (!setupState.root || setupState.scanId == null) return;
    send({
      cmd: "preflight",
      root: setupState.root,
      folder: setupState.folder,
      group: setupState.group,
      scanId: setupState.scanId
    });
  }

  function showPreflight(p) { setupState.pre = p; drawSetup(); }

  function tick() {
    var svg = document.createElementNS("http://www.w3.org/2000/svg", "svg");
    svg.setAttribute("viewBox", "0 0 10 10");
    svg.innerHTML = '<path d="M1 5l2.6 2.6L9 2.2" fill="none" stroke="currentColor" stroke-width="2"/>';
    return svg;
  }

  function drawSetup() {
    setup.innerHTML = "";
    var box = el("div", "setup");

    var back = el("button", "back", "←  Listeye dön");
    back.type = "button";
    back.addEventListener("click", closeSetup);
    box.appendChild(back);

    box.appendChild(el("h2", null, "Hedef"));
    setupState.drives.forEach(function (d) {
      var b = el("button", "drive");
      b.type = "button";
      b.setAttribute("aria-pressed", String(!setupState.custom && setupState.root === d.root));
      if (!d.ready) b.disabled = true;
      b.appendChild(el("span", "pip"));
      var nm = el("span", "nm");
      nm.appendChild(document.createTextNode(d.root));
      nm.appendChild(el("small", null, d.ready ? (d.label || "Yerel disk") : "Hazır değil"));
      b.appendChild(nm);
      b.appendChild(el("span", "sp", d.ready
        ? fmtBytes(d.free) + " boş / " + fmtBytes(d.total)
        : ""));
      b.addEventListener("click", function () {
        setupState.root = d.root;
        setupState.custom = false;
        drawSetup();
        askPreflight();
      });
      box.appendChild(b);
    });

    var browse = el("button", "ghost", "Başka bir klasör seç...");
    browse.type = "button";
    browse.addEventListener("click", function () { send({ cmd: "browse" }); });
    box.appendChild(browse);
    if (setupState.custom && setupState.root) {
      box.appendChild(el("p", "dest", setupState.root));
    }

    box.appendChild(el("h2", null, "Düzen"));
    var grp = el("button", "check");
    grp.type = "button";
    grp.setAttribute("aria-pressed", String(setupState.group));
    var gb = el("span", "box");
    gb.appendChild(tick());
    grp.appendChild(gb);
    var gt = el("span", "t");
    gt.appendChild(document.createTextNode("Tek klasörde topla"));
    gt.appendChild(el("small", null,
      "Kapalıyken Kamera, WhatsApp gibi klasörler doğrudan seçilen yere açılır."));
    grp.appendChild(gt);
    grp.addEventListener("click", function () {
      setupState.group = !setupState.group;
      drawSetup();
      askPreflight();
    });
    box.appendChild(grp);

    if (setupState.group) {
      var field = el("div", "field");
      var lab = document.createElement("label");
      lab.setAttribute("for", "folderName");
      lab.textContent = "Klasör adı";
      var inp = document.createElement("input");
      inp.type = "text";
      inp.id = "folderName";
      inp.value = setupState.folder;
      inp.addEventListener("input", function () { setupState.folder = inp.value; });
      inp.addEventListener("change", askPreflight);
      field.appendChild(lab);
      field.appendChild(inp);
      box.appendChild(field);
    }

    var p = setupState.pre;
    if (p) {
      var dest = el("p", "dest");
      dest.appendChild(document.createTextNode("Hedef: "));
      dest.appendChild(el("b", null, p.destination));
      box.appendChild(dest);

      var led = el("div", "ledger");
      function row(a, b, cls) {
        var r = el("div", "r" + (cls ? " " + cls : ""));
        r.appendChild(el("span", null, a));
        r.appendChild(el("span", null, b));
        led.appendChild(r);
      }
      row("Aktarılacak dosya", fmtInt(p.files));
      if (p.renamed > 0) row("Adı değiştirilecek", fmtInt(p.renamed));
      row("Gereken yer", fmtBytes(p.required));
      row("Boş yer", fmtBytes(p.free));
      row(p.fits ? "Sığıyor" : (p.problem || "Sığmıyor"), "", p.fits ? "total" : "total bad");
      box.appendChild(led);

      var go = el("button", "go", "Aktarımı başlat");
      go.type = "button";
      go.disabled = !p.fits;
      go.addEventListener("click", function () {
        setStrip("info", "Kopyalama motoru henüz yok.",
          "Plan ve kontrol hazır; dosyaları taşıyan kısım bir sonraki adım.");
        closeSetup();
      });
      box.appendChild(go);
    } else {
      box.appendChild(el("p", "dest", "Hesaplanıyor..."));
    }

    setup.appendChild(box);
  }

  function showNotice(message, offerScan) {
    tabs.innerHTML = "";
    content.innerHTML = "";
    statusbar.innerHTML = "";
    setStrip("info", null, message);
    var p = el("p", "empty", offerScan ? "Taramak için yukarıdaki düğmeyi kullanın." : "");
    content.appendChild(p);
  }

  /* ---------------- boot with data ---------------- */

  var scan, complete, reasons;

  function boot() {
    scan = D.scans.filter(function (s) { return s.scan_id === D.scanId; })[0] || D.scans[0];
    complete = scan.status === "complete";

    // Yedi alan, cunku guven karari yedi alana bakiyor. Dordune bakmak,
    // "Eksik sayim" deyip sebebini soylememek demekti.
    reasons = [];
    if (!scan.completed) reasons.push("yürüyüş sona ulaşmadı");
    if (scan.stalled) reasons.push("cihaz cevap vermeyi kesti");
    if (scan.faulted) reasons.push("tarama bir hatayla durdu");
    if (scan.camera_mode) reasons.push("telefon görüntü aktarımı modundaydı, video ve belgeler gizliydi");
    if (scan.subtree_losses > 0) reasons.push(scan.subtree_losses + " klasör listelenemedi");
    if (scan.unresolved_objects > 0) reasons.push(scan.unresolved_objects + " nesne tanımlanamadı");
    if (scan.undetermined_files > 0) reasons.push(scan.undetermined_files + " dosyanın türü belirlenemedi");

    var dl = document.getElementById("deviceLabel");
    dl.innerHTML = "";
    if (D.device) {
      dl.appendChild(el("b", null, D.device.friendly_name || ""));
      dl.appendChild(document.createTextNode("  " +
        (D.device.manufacturer || "") + " " + (D.device.model || "")));
    }

    if (!state.scanning) {
      if (!complete) {
        // Sebepsiz bir eksiklik uyarisi, kullanicinin gormezden gelmeyi
        // ogrenecegi turden bir uyaridir. Sebebi bilmiyorsak onu soyluyoruz.
        setStrip("", "Eksik sayım.", reasons.length
          ? reasons.join(", ") + ". Eksik dosyalar silinmiş sayılmamalı."
          : "Sebebi bu kayıtta yok. Yine de eksik dosyalar silinmiş sayılmamalı.");
      } else {
        strip.hidden = true;
      }
    }

    buildTabs();
    render();
  }

  function tabOrder() {
    var apps = Object.keys(D.agg).filter(function (k) { return k.indexOf("app:") === 0; });
    apps.sort(function (a, b) { return D.agg[b].media - D.agg[a].media; });
    return ["camera", "screenshot"].concat(apps, ["download", "other"])
      .filter(function (k) { return D.agg[k]; });
  }

  function buildTabs() {
    tabs.innerHTML = "";
    var ids = ["all"].concat(tabOrder());
    if (ids.indexOf(state.tab) < 0 && state.tab !== "scan") state.tab = "all";

    function addTab(id, label, trailing) {
      var b = el("button", "tab" + (trailing ? " trailing" : ""), label);
      b.type = "button";
      b.setAttribute("role", "tab");
      b.setAttribute("aria-selected", String(state.tab === id));
      b.addEventListener("click", function () {
        state.tab = id;
        Array.prototype.forEach.call(tabs.children, function (t) {
          t.setAttribute("aria-selected", String(t === b));
        });
        render();
      });
      tabs.appendChild(b);
    }

    addTab("all", "Tümü", false);
    tabOrder().forEach(function (k) { addTab(k, D.labels[k] || k, false); });
    addTab("scan", "Tarama", true);
  }

  /* ---------------- list ---------------- */

  function filesFor(tab) {
    return D.files.filter(function (f) { return tab === "all" || f.src === tab; });
  }

  function sortValue(f, key) {
    if (key === "n") return f.n.toLocaleLowerCase("tr");
    if (key === "t") return tur(f);
    if (key === "s") return f.s || 0;
    return f.d || "";
  }

  function renderList() {
    var list = filesFor(state.tab).slice();
    list.sort(function (a, b) {
      var x = sortValue(a, state.sort), y = sortValue(b, state.sort);
      if (x === y) return a.n.localeCompare(b.n, "tr");
      if (typeof x === "number") return (x - y) * state.dir;
      return x.localeCompare(y, "tr") * state.dir;
    });

    var cols = el("div", "cols");
    COLS.forEach(function (c) {
      var b = el("button", "colbtn " + c.cls);
      b.type = "button";
      var active = state.sort === c.key;
      b.setAttribute("aria-sort", active ? (state.dir === 1 ? "ascending" : "descending") : "none");
      b.appendChild(document.createTextNode(c.label));
      var arrow = document.createElementNS("http://www.w3.org/2000/svg", "svg");
      arrow.setAttribute("class", "arrow");
      arrow.setAttribute("viewBox", "0 0 8 8");
      arrow.innerHTML = state.dir === 1
        ? '<path d="M1 5.5L4 2.5 7 5.5" fill="none" stroke="currentColor" stroke-width="1.4"/>'
        : '<path d="M1 2.5L4 5.5 7 2.5" fill="none" stroke="currentColor" stroke-width="1.4"/>';
      b.appendChild(arrow);
      b.addEventListener("click", function () {
        if (state.sort === c.key) state.dir = -state.dir;
        else { state.sort = c.key; state.dir = c.dir; }
        render();
      });
      cols.appendChild(b);
    });
    content.appendChild(cols);

    if (!list.length) {
      content.appendChild(el("p", "empty", "Bu bölümde dosya yok."));
      return;
    }

    list.slice(0, MAX).forEach(function (f) {
      var r = el("div", "row");
      var nm = el("span", "nm");
      nm.appendChild(document.createTextNode(f.n));
      if (state.tab === "all") {
        nm.appendChild(document.createTextNode("  "));
        nm.appendChild(el("span", "src", D.labels[f.src] || ""));
      }
      r.appendChild(nm);
      r.appendChild(el("span", "meta r c-type", tur(f)));
      r.appendChild(el("span", "meta r num", fmtBytes(f.s)));
      r.appendChild(el("span", "meta r num c-date", fmtDate(f.d)));
      content.appendChild(r);
    });

    if (list.length > MAX) {
      content.appendChild(el("p", "more",
        fmtInt(MAX) + " satır gösteriliyor. Durum çubuğundaki sayılar taramanın tamamını yansıtır."));
    }
  }

  /* ---------------- scan tab ---------------- */

  function card(title) {
    var c = el("div", "card");
    c.appendChild(el("h2", null, title));
    return c;
  }

  function renderScanTab() {
    var pad = el("div", "pad");
    var panels = el("div", "panels");

    var c1 = card("Son tarama");
    var dlist = el("dl", null);
    [["Durum", complete ? "Tam sayım" : "Eksik sayım"],
     ["Medya", fmtInt(scan.media_files)],
     ["Belge", fmtInt(scan.documents)],
     ["Görülen dosya", fmtInt(scan.total_files_seen)],
     ["İmza kontrolü", fmtInt(scan.signature_checks_run)],
     ["Başlangıç", (scan.started_utc || "").slice(0, 16).replace(/-/g, ".")]
    ].forEach(function (p) {
      var d = el("div", "kv");
      d.appendChild(el("dt", null, p[0]));
      d.appendChild(el("dd", null, p[1]));
      dlist.appendChild(d);
    });
    c1.appendChild(dlist);
    if (reasons.length) {
      var ul = el("ul", "reasons");
      reasons.forEach(function (r) { ul.appendChild(el("li", null, r)); });
      c1.appendChild(ul);
    }
    panels.appendChild(c1);

    var c2 = card("Geçmiş");
    var t = document.createElement("table");
    t.innerHTML = "<thead><tr><th>Tarama</th><th>Durum</th><th class='r'>Medya</th><th class='r'>Görülen</th></tr></thead>";
    var tb = document.createElement("tbody");
    D.scans.slice(0, 10).forEach(function (s) {
      var tr = document.createElement("tr");
      var a = document.createElement("td");
      a.className = "num";
      a.textContent = (s.started_utc || "").slice(5, 16).replace(/-/g, ".");
      tr.appendChild(a);
      var b = document.createElement("td");
      var ok = s.status === "complete";
      var running = s.status === "running";
      b.appendChild(el("span", "dot " + (ok ? "ok" : "warn")));
      b.appendChild(document.createTextNode(ok ? "tam" : running ? "yarıda" : "eksik"));
      tr.appendChild(b);
      var c = document.createElement("td"); c.className = "r num"; c.textContent = fmtInt(s.media_files); tr.appendChild(c);
      var d = document.createElement("td"); d.className = "r num"; d.textContent = fmtInt(s.total_files_seen); tr.appendChild(d);
      tb.appendChild(tr);
    });
    t.appendChild(tb);
    c2.appendChild(t);
    panels.appendChild(c2);

    var c3 = card("Taranmayan klasörler");
    var ul2 = el("ul", "skips");
    (D.skipped || []).forEach(function (s) {
      var li = document.createElement("li");
      var code = document.createElement("code");
      code.textContent = s.path;
      li.appendChild(code);
      li.appendChild(el("span", "sub", s.reason));
      ul2.appendChild(li);
    });
    c3.appendChild(ul2);
    panels.appendChild(c3);

    pad.appendChild(panels);
    content.appendChild(pad);
  }

  /* ---------------- frame ---------------- */

  function render() {
    // Tarama surerken ekran canli akisa ait ve ona dokunulmaz; bastan cizmek
    // tam da kacinilan titremeyi geri getirirdi.
    if (state.scanning) return;
    content.innerHTML = "";
    if (state.tab === "scan") renderScanTab();
    else renderList();
    renderStatus();
  }

  function renderStatus() {
    statusbar.innerHTML = "";

    var totalFiles = 0, totalBytes = 0;
    Object.keys(D.agg).forEach(function (k) {
      totalFiles += D.agg[k].media + D.agg[k].doc;
      totalBytes += D.agg[k].bytes;
    });

    if (state.tab === "scan") {
      statusbar.appendChild(el("span", null, fmtInt(D.scans.length) + " tarama kayıtlı"));
    } else if (state.tab === "all") {
      var h = el("span");
      h.appendChild(el("b", "num", fmtInt(totalFiles)));
      h.appendChild(document.createTextNode(" dosya, " + fmtBytes(totalBytes)));
      statusbar.appendChild(h);
    } else {
      var a = D.agg[state.tab] || { media: 0, doc: 0, bytes: 0 };
      var here = el("span");
      here.appendChild(el("b", "num", fmtInt(a.media + a.doc)));
      here.appendChild(document.createTextNode(" dosya, " + fmtBytes(a.bytes)));
      statusbar.appendChild(here);
      statusbar.appendChild(el("span", null,
        "Toplam " + fmtInt(totalFiles) + " dosya, " + fmtBytes(totalBytes)));
    }

    var v = el("span", "verdict push " + (complete ? "ok" : "warn"));
    v.appendChild(el("span", "dot " + (complete ? "ok" : "warn")));
    v.appendChild(document.createTextNode(complete ? "Tam sayım" : "Eksik sayım"));
    statusbar.appendChild(v);
  }

  /* ---------------- start ---------------- */

  setStrip("info", null, "Kayıtlar okunuyor.");
  if (host) send({ cmd: "load" });
  else showNotice("Bu sayfa masaüstü uygulamasının içinde çalışır.", false);
})();

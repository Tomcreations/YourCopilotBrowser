(function() {
  'use strict';

  var links = {{linksJson}};
  if (!links || links.length === 0) return;

  var existing = document.getElementById('_ycb_dl_bar');
  if (existing) existing.remove();

  var bar = document.createElement('div');
  bar.id = '_ycb_dl_bar';
  bar.style.cssText = [
    'position:fixed','bottom:0','left:0','right:0','z-index:2147483647',
    'background:rgba(28,29,32,0.97)','border-top:1px solid rgba(255,255,255,0.1)',
    'padding:10px 16px','display:flex','align-items:center','gap:12px',
    'font-family:-apple-system,BlinkMacSystemFont,"Segoe UI",Roboto,sans-serif',
    'font-size:13px','box-shadow:0 -4px 24px rgba(0,0,0,0.4)',
    'backdrop-filter:blur(10px)','-webkit-backdrop-filter:blur(10px)'
  ].join(';');

  var icon = document.createElement('div');
  icon.innerHTML = '<svg width="18" height="18" viewBox="0 0 24 24" fill="none" stroke="#8ab4f8" stroke-width="2" stroke-linecap="round" stroke-linejoin="round"><path d="M21 15v4a2 2 0 01-2 2H5a2 2 0 01-2-2v-4"/><polyline points="7 10 12 15 17 10"/><line x1="12" y1="15" x2="12" y2="3"/></svg>';
  icon.style.cssText = 'flex-shrink:0;display:flex;align-items:center;';
  bar.appendChild(icon);

  var lbl = document.createElement('span');
  lbl.textContent = 'Download available';
  lbl.style.cssText = 'color:#9aa0a6;font-size:12px;flex-shrink:0;';
  bar.appendChild(lbl);

  var selector;
  if (links.length === 1) {
    selector = document.createElement('span');
    selector.textContent = links[0].fileName;
    selector.title = links[0].label;
    selector.style.cssText = 'color:#e8eaed;font-weight:500;flex:1;overflow:hidden;text-overflow:ellipsis;white-space:nowrap;min-width:0;';
  } else {
    selector = document.createElement('select');
    selector.style.cssText = [
      'background:#2d2e31','color:#e8eaed','border:1px solid rgba(255,255,255,0.15)',
      'border-radius:6px','padding:5px 10px','font-size:13px','flex:1','min-width:0',
      'cursor:pointer','outline:none','max-width:460px'
    ].join(';');
    links.forEach(function(l) {
      var opt = document.createElement('option');
      opt.value = l.url;
      opt.textContent = l.label + '  —  ' + l.fileName;
      selector.appendChild(opt);
    });
  }
  bar.appendChild(selector);

  var btn = document.createElement('button');
  btn.textContent = '\u2913 Download';
  btn.style.cssText = [
    'background:#1a73e8','color:#fff','border:none','border-radius:6px',
    'padding:7px 18px','font-size:13px','font-weight:600','cursor:pointer',
    'flex-shrink:0','white-space:nowrap','transition:background 0.15s'
  ].join(';');
  btn.onmouseover = function() { btn.style.background = '#1558b0'; };
  btn.onmouseout  = function() { btn.style.background = '#1a73e8'; };
  btn.onclick = function() {
    var url = links.length === 1 ? links[0].url : selector.value;
    if (window.chrome && window.chrome.webview) {
      window.chrome.webview.postMessage({ type: 'quickdownload:open', url: url });
    } else {
      window.open(url, '_blank');
    }
    bar.remove();
  };
  bar.appendChild(btn);

  var cls = document.createElement('button');
  cls.textContent = '\u00d7';
  cls.title = 'Dismiss';
  cls.style.cssText = [
    'background:transparent','color:#9aa0a6','border:none','border-radius:4px',
    'padding:4px 8px','font-size:18px','cursor:pointer','flex-shrink:0',
    'line-height:1','transition:color 0.15s'
  ].join(';');
  cls.onmouseover = function() { cls.style.color = '#e8eaed'; };
  cls.onmouseout  = function() { cls.style.color = '#9aa0a6'; };
  cls.onclick = function() { bar.remove(); };
  bar.appendChild(cls);

  document.body.appendChild(bar);
})();

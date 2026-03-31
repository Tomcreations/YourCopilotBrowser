(function() {
  'use strict';
  // Lock down globals so tracker scripts cannot override them
  function def(name, val) {
    try {
      Object.defineProperty(window, name, {
        value: val,
        writable: false,
        configurable: false
      });
    } catch(e) {}
  }

  // All tracker globals set to undefined so typeof checks return 'undefined' → eval detects as blocked
  // Turtlecute ad script test variables — pre-lock before ads.js/pagead.js can set them
  def('s_test_ads', undefined); def('s_test_pagead', undefined);
  def('s_test_analytics', undefined); def('s_test_tracker', undefined);

  // Google Ads / Tag Manager
  def('ga', undefined); def('gtag', undefined); def('_gaq', undefined);
  def('dataLayer', undefined); def('GoogleAnalyticsObject', undefined);
  def('google_tag_manager', undefined); def('google_tag_data', undefined);
  def('googletag', undefined); def('google_ad_client', undefined);
  def('google_ad_slot', undefined); def('adsbygoogle', undefined);
  def('_google_ad_width', undefined); def('_google_ad_height', undefined);
  def('google_ad_format', undefined); def('google_ad_type', undefined);
  def('google_page_url', undefined);

  // Facebook
  def('fbq', undefined); def('_fbq', undefined);

  // Yandex Metrika
  def('ym', undefined);
  def('yandex_metrika_callbacks', undefined);
  def('yandexContextAsyncCallbacks', undefined);

  // Hotjar
  def('hj', undefined); def('_hjSettings', undefined);
  def('_hjid', undefined); def('_hjSessionUser', undefined);

  // Mixpanel
  def('mixpanel', undefined);

  // Microsoft Clarity
  def('clarity', undefined);

  // Amplitude
  def('amplitude', undefined);

  // Segment / analytics.js
  def('analytics', undefined);

  // Intercom
  def('Intercom', undefined);

  // New Relic
  def('NREUM', undefined); def('newrelic', undefined);

  // Heap
  def('heap', undefined);

  // Crazy Egg / Lucky Orange
  def('CE2', undefined); def('LO', undefined); def('LOQ', undefined);

  // Sentry
  def('Sentry', undefined);
  def('__sentryRewritesTunnelPath__', undefined);
  def('__SENTRY__', undefined);

  // Bugsnag
  def('Bugsnag', undefined); def('bugsnag', undefined);

  // Rollbar
  def('Rollbar', undefined); def('_rollbarConfig', undefined);

  // Raygun
  def('rg4js', undefined); def('Raygun', undefined);

  // Datadog RUM
  def('DD_RUM', undefined); def('DD_LOGS', undefined);

  // LogRocket
  def('LogRocket', undefined);

  // FullStory
  def('FS', undefined); def('_fs_namespace', undefined);

  // Mouseflow / CrazyEgg session replay
  def('mouseflow', undefined); def('_mfq', undefined);
  def('CE_API', undefined);

  // TikTok
  def('ttq', undefined); def('TiktokAnalyticsObject', undefined);

  // Twitter/X
  def('twq', undefined); def('twttr', undefined);

  // LinkedIn
  def('_linkedin_data_partner_id', undefined); def('lintrk', undefined);

  // Pinterest
  def('pintrk', undefined);

  // Reddit
  def('rdt', undefined);

  // Yandex Metrika
  def('yaCounter', undefined);

  // Taboola / Outbrain
  def('_taboola', undefined); def('OBR', undefined);

  // Criteo
  def('Criteo', undefined); def('CriteoQ', undefined);

  // Chartbeat
  def('_sf_async_config', undefined); def('pSUPERFLY', undefined);

  // Quantcast
  def('__qc', undefined);

  // Comscore
  def('COMSCORE', undefined);

  // Block sendBeacon (tracker fallback)
  try { navigator.sendBeacon = function() { return true; }; } catch(e) {}

  // Block Image pixel trackers (1x1 GIF beacons etc.)
  try {
    var _OrigImage = window.Image;
    window.Image = function() {
      var img = new _OrigImage();
      var origSrcDesc = Object.getOwnPropertyDescriptor(_OrigImage.prototype, 'src');
      Object.defineProperty(img, 'src', {
        set: function(val) {
          if (val && isBlockedUrl(String(val))) return;
          if (origSrcDesc && origSrcDesc.set) origSrcDesc.set.call(img, val);
        },
        get: function() { return origSrcDesc && origSrcDesc.get ? origSrcDesc.get.call(img) : ''; }
      });
      return img;
    };
  } catch(e) {}

  // Block dynamic <script> injection for known ad/tracker scripts
  try {
    var _origCreateElement = document.createElement.bind(document);
    document.createElement = function(tag) {
      var el = _origCreateElement(tag);
      if (tag && tag.toLowerCase() === 'script') {
        var origSrcDesc2 = Object.getOwnPropertyDescriptor(HTMLScriptElement.prototype, 'src');
        Object.defineProperty(el, 'src', {
          set: function(val) {
            if (val && isBlockedUrl(String(val))) {
              // Silently ignore — do not attach to DOM
              Object.defineProperty(el, '_blocked', { value: true, writable: true });
              return;
            }
            if (origSrcDesc2 && origSrcDesc2.set) origSrcDesc2.set.call(el, val);
          },
          get: function() { return origSrcDesc2 && origSrcDesc2.get ? origSrcDesc2.get.call(el) : ''; },
          configurable: true
        });
      }
      return el;
    };
  } catch(e) {}

  // Block WebSocket connections to tracker domains
  try {
    var _OrigWS = window.WebSocket;
    window.WebSocket = function(url, protocols) {
      if (url && isBlockedUrl(String(url))) throw new Error('blocked');
      return protocols !== undefined ? new _OrigWS(url, protocols) : new _OrigWS(url);
    };
    window.WebSocket.prototype = _OrigWS.prototype;
  } catch(e) {}

  // Comprehensive block list regex - matches all major ad/tracker/social/OEM domains
  var BLOCK_RE = /googlesyndication\.com|doubleclick\.net|googleadservices\.com|googletagmanager\.com|googleanalytics\.com|google-analytics\.com|analytics\.google\.com|click\.googleanalytics\.com|adservice\.google\.|adcolony\.com|media\.net|hotjar\.(com|io)|mouseflow\.com|freshmarketer\.com|freshworks\.com|freshdesk\.com|freshchat\.com|wchat\.freshchat|luckyorange\.|stats\.wp\.com|bugsnag\.com|sentry-cdn\.com|getsentry\.com|sentry\.io|pixel\.facebook\.com|an\.facebook\.com|connect\.facebook\.(com|net)|ads-twitter\.com|ads-api\.twitter\.com|ads\.linkedin\.com|pointdrive\.linkedin\.com|ads\.pinterest\.com|log\.pinterest\.com|trk\.pinterest\.com|ct\.pinterest\.com|events\.reddit\.com|redditmedia\.com|alb\.reddit\.com|pixel\.reddit\.com|ads\.youtube\.com|tiktok\.(com|sg)|byteoversea\.com|ads\.yahoo\.com|analytics\.yahoo\.com|geo\.yahoo\.com|udcm\.yahoo\.com|ysm\.yahoo\.com|log\.fc\.yahoo\.com|gemini\.yahoo\.com|yahooinc\.com|appmetrica\.yandex|adfstat\.yandex|metrika\.yandex|mc\.yandex\.ru|offerwall\.yandex|adfox\.yandex|extmaps-api\.yandex|unityads\.unity3d\.com|realme\.com|realmemobile\.com|mistat\.xiaomi|ad\.xiaomi\.com|sdkconfig\.ad|tracking\.rus\.miui|oppomobile\.com|hicloud\.com|oneplus\.(cn|net)|samsungads\.com|smetrics\.samsung|nmetrics\.samsung|samsung-com\.112|samsunghealthcn|iadsdk\.apple\.com|metrics\.icloud\.com|metrics\.mzstatic\.com|api-adservices\.apple\.com|analytics-events\.apple\.com|newrelic\.com|nr-data\.net|rollbar\.com|raygun\.com|datadog|logrocket\.com|fullstory\.com|clarity\.ms|amplitude\.com|mixpanel\.com|segment\.(io|com)|heap\.io|heapanalytics|intercom\.(io|com)|crazyegg|inspectlet|clicky\.com|woopra\.com|chartbeat|scorecardresearch|comscore\.com|quantserve|adnxs\.com|amazon-adsystem\.com|pubmatic\.com|openx\.(net|com)|rubiconproject|casalemedia|adsrvr\.org|moatads|yieldmo|criteo\.com|taboola\.com|outbrain\.com|adroll\.com|adtago\.s3\.amazonaws|analyticsengine\.s3\.amazonaws|analytics\.s3\.amazonaws|advice-ads\.s3\.amazonaws|facebook\.com\/(tr|pixel)|\/pagead\.js|\/widget\/ads\.js|\/ads\.js\b|pagead2\.|adsbygoogle|advertising\.com|bidswitch\.net|contextweb\.com|sharethrough\.com|triplelift\.com|33across\.com|sovrn\.com|smartadserver\.com|teads\.(tv|com)|spotxchange\.com|spotx\.tv|undertone\.com|mediavine\.com|revcontent\.com|lijit\.com|adtech\.(com|de)|everesttech\.net|statcounter\.com|krxd\.net|quantcast\.com|adsymptotic\.com|serving-sys\.com|turn\.com|demdex\.net|bluekai\.com|exelator\.com|addthis\.com|sharethis\.com|disqus\.com\/count|livefyre\.com|apnxs\.com|adgrx\.com|lkqd\.net|freewheel\.tv|stickyadstv\.com|jwpltx\.com|jwpsrv\.com|advertising-api\.amazon|bat\.bing\.com|bat\.r\.msn\.com|c\.bing\.com\/c\b|snap\.licdn\.com|munchkin\.marketo|hs-analytics\.net|hsforms\.net|hscta\.net|hubspot\.com\/analytics|pardot\.com|marketo\.com|eloqua\.com|adsafeprotected\.com|doubleverify\.com|integral-assets\.com|optimizely\.com|mathtag\.com|hubspot\.com\/log|t\.myvisualiq\.net|insightexpressai\.com|impactradius\.com|shareasale\.com|cj\.com|commission-junction\.com|dpbolvw\.net|jdoqocy\.com|kqzyfj\.com|qksrv\.net|tkqlhce\.com|anrdoezrs\.net|awin\.(com|1\.com)|zanox\.com|tradedoubler\.com|viglink\.com|skimlinks\.com|skimresources\.com|pepperjam\.com|pjtra\.com|pjatr\.com|avantlink\.com|maxbounty\.com|partnerize\.com|conversant(media)?\.com|flexoffers\.com|webgains\.com|commissionfactory\.com|tune\.com|hasoffers\.com|everflow\.io|affise\.com|linkconnector\.com|linksynergy\.com|performancehorizon\.com|clickbooth\.com|clickbank\.com|clkmon\.com|clkrev\.com|go2cloud\.org|affiliatewindow\.com|2mdn\.net|brightcove\.com|springserve\.com|videoamp\.com|unrulymedia\.com|tremormedia\.com|tremorvideo\.com|innovid\.com|vindico\.com|yume\.com|extreme-reach\.com|vidazoo\.com|connatix\.com|loopme\.com|gumgum\.com|primis\.tech|adtelligent\.com|magnite\.com|adap\.tv|liverail\.com|aniview\.com|onetrust\.com|cookielaw\.org|cookiebot\.com|trustarc\.com|truste\.com|consensu\.org|consentmanager\.net|didomi\.io|usercentrics\.com|iubenda\.com|cookiefirst\.com|osano\.com|sourcepoint\.com|evidon\.com|crownpeak\.com|cookie-script\.com|cookiehub\.com|termly\.io|cookieyes\.com|complianz\.io|secureprivacy\.ai|fingerprint\.com|fingerprintjs\.com|fpjscdn\.net|maxmind\.com|threatmetrix\.com|iovation\.com|sessioncam\.com|clicktale\.com|contentsquare\.com|dynatrace\.com|sift(science)?\.com|perimeterx\.com|px-cdn\.net|px-cloud\.net|imperva\.com|distilnetworks\.com|human\.security|tiqcdn\.com|tns-counter\.ru|ipqualityscore\.com|deviceatlas\.com|51degrees\.com|forensiq\.com|fraudlogix\.com|visitoridentification\.net|augur.io|liveramp\.com|rlcdn\.com|agkn\.com|rapleaf\.com|neustar\.biz|tapad\.com|drawbridge\.com|coinhive\.com|coin-hive\.com|cryptoloot\.pro|minero\.cc|jsecoin\.com|monerominer.rocks|webmr\.eu|coinimp\.com|papoto\.com|cryptonight\.pro|afminer\.com|coinerra\.com|minerpool\.net|nbminer\.com|crypto-loot\.com|minergate\.com|nicehash\.com|2giga.link|hashfor.cash|coin-have\.com|xmrpool\.(net|eu)|supportxmr\.com|monerocean\.stream|hashvault\.pro|xmrig\.com|coinblind\.com|gridcash\.net|opens\.mailchimp\.com|list-manage\.com|tracking\.sendgrid\.net|sendgrid\.net|mailgun\.org|sparkpostmail\.com|sailthru\.com|litmus\.com|vero\.co|customer\.io|klaviyo\.com|drip\.com|convertkit\.com|activecampaign\.com|constantcontact\.com|mailjet\.com|postmarkapp\.com|mandrillapp\.com|campaignmonitor\.com|createsend\.com|aweber\.com|yesware\.com|bananatag\.com|abtasty\.com|vwo\.com|convert\.com|kameleoon\.com|unbounce\.com|qubit\.com|monetate\.net|launchdarkly\.com|split\.io|statsig\.com|inmobi\.com|applovin\.com|ironsource\.com|vungle\.com|chartboost\.com|startapp\.com|ogury\.com|propellerads\.com|exoclick\.com|adform\.(net|com)|adjust\.com|appsflyer\.com|kochava\.com|branch\.io|singular\.net|tenjin\.io|tr.snapchat\.com|sc-static\.net|snapads\.com|spade\.twitch\.tv|ads\.twitch\.tv|analytics\.twitter\.com|platform\.twitter\.com|syndication\.twitter\.com|cdn\.syndication\.twimg\.com|graph\.instagram\.com|i\.instagram\.com|px\.ads\.linkedin\.com|dc\.ads\.linkedin\.com|platform\.linkedin\.com|analytics\.pinterest\.com|vivo\.com\.cn|lganalytics\.com|lgtvsdp\.com|lgsmartad\.com|lgappstv\.com|motorola\.com\/analytics|moto-analytics\.com|analyticsservices\.sony\.com|ps-metrics\.sonyentertainmentnetwork\.com|analytics\.lenovo\.com|track\.lenovo\.com|analytics\.asus\.com|splashads\.asus\.com|analytics\.hmdglobal\.com|analytics\.htc\.com|analytics\.tcl\.com|forter\.com|riskiq\.com|inauth\.com|accertify\.com|kount\.com|signifyd\.com|bounceexchange\.com|wunderkind\.co|semasio\.net|eyeota\.com|weborama\.com|pippio\.com|nexac\.com|netmng\.com|audienceinsights\.net|creativecdn\.com|permutive\.com|zergnet\.com|ooyala\.com|brightroll\.com|beachfront\.com|verve\.com|rhythmone\.com|360yield\.com|videologygroup\.com|playwire\.com|privacymanager\.io|cookieinformation\.com|uniconsent\.com|privacy-mgmt\.com|financeads\.net|affilinet\.com|belboon\.com|adcell\.de|tradetracker\.com|admitad\.com|cityads\.com|marketgid\.com/i;

  function isBlockedUrl(url) {
    try { return BLOCK_RE.test(url); } catch(e) { return false; }
  }

  // Runtime hosts and heuristics — fetch a maintained host-list and apply heuristic matching
  var __runtimeHosts = [];
  var __heuristicsRe = /(?:ad|banner|sponsor|analytics|track(?:er)?|pixel|beacon|cookie|consent|adsense|adsbygoogle|doubleclick|googlesyndication|pagead|adservice|adserver|affiliat|offerwall|popad|cryptominer|coin|miner|xmr|monero|videoad)/i;

  function runtimeMatches(url) {
    try {
      if (!url) return false;
      for (var i=0;i<__runtimeHosts.length;i++) {
        if (!__runtimeHosts[i]) continue;
        if (url.indexOf(__runtimeHosts[i]) !== -1) return true;
      }
      return false;
    } catch(e) { return false; }
  }

  // Enhanced isBlocked: BLOCK_RE OR runtime host list OR heuristic keywords in URL or inline script content
  function isBlockedUrlOrContent(urlOrContent) {
    try {
      if (typeof urlOrContent !== 'string') urlOrContent = String(urlOrContent || '');
      if (BLOCK_RE.test(urlOrContent)) return true;
      if (runtimeMatches(urlOrContent)) return true;
      if (__heuristicsRe.test(urlOrContent)) return true;
      return false;
    } catch(e) { return false; }
  }

  // Fetch runtime host list (best-effort, non-blocking)
  try {
    fetch('https://raw.githubusercontent.com/Turtlecute33/Toolz/master/src/d3host.txt').then(function(r){
      if (!r.ok) return; return r.text();
    }).then(function(txt){
      if (!txt) return;
      txt.split(/\r?\n/).forEach(function(line){
        line = line.trim();

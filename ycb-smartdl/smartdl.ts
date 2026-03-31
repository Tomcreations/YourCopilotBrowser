import express from 'express';
import * as cheerio from 'cheerio';
import * as fs from 'fs';
import * as path from 'path';
import * as os from 'os';

// YCB Smart Download — local backend for the Quick Download optional feature

const app = express();
app.use(express.json());

// Allow any origin (localhost only anyway)
app.use((_req, res, next) => {
  res.setHeader('Access-Control-Allow-Origin', '*');
  res.setHeader('Access-Control-Allow-Methods', 'POST, OPTIONS');
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type, X-YCB-Client');
  next();
});
app.options('*', (_req, res) => res.sendStatus(204));

// Health check
app.get('/health', (_req, res) => res.json({ ok: true }));

// ─── In-memory result cache ───────────────────────────────────────────────────
const resultCache = new Map<string, { links: DownloadOption[], ts: number }>();
const CACHE_TTL = 5 * 60 * 1000; // 5 minutes

// ─── Learned downloads ────────────────────────────────────────────────────────
interface LearnedEntry { downloadUrl: string; count: number; lastSeen: number; }
const learnedByHost = new Map<string, LearnedEntry[]>();

const DATA_DIR = path.join(process.env.LOCALAPPDATA || os.homedir(), 'YCB-Browser');
const LEARNED_FILE = path.join(DATA_DIR, 'ycb-learned.json');

try {
  fs.mkdirSync(DATA_DIR, { recursive: true });
  const raw = JSON.parse(fs.readFileSync(LEARNED_FILE, 'utf8'));
  for (const [host, entries] of Object.entries(raw))
    learnedByHost.set(host, entries as LearnedEntry[]);
} catch {}

function saveLearned() {
  try {
    const obj: Record<string, LearnedEntry[]> = {};
    for (const [host, entries] of learnedByHost) obj[host] = entries;
    fs.writeFileSync(LEARNED_FILE, JSON.stringify(obj));
  } catch {}
}

app.post('/api/learn', (req, res) => {
  try {
    const { pageUrl, downloadUrl } = req.body as { pageUrl?: string; downloadUrl?: string };
    if (!pageUrl || !downloadUrl) return res.json({ ok: false });
    const host = new URL(pageUrl).hostname.replace(/^www\./, '');
    const entries = learnedByHost.get(host) || [];
    const existing = entries.find(e => e.downloadUrl === downloadUrl);
    if (existing) { existing.count++; existing.lastSeen = Date.now(); }
    else entries.push({ downloadUrl, count: 1, lastSeen: Date.now() });
    entries.sort((a, b) => b.count - a.count);
    learnedByHost.set(host, entries.slice(0, 20));
    saveLearned();
    res.json({ ok: true });
  } catch { res.json({ ok: false }); }
});

// ─── File type categories ─────────────────────────────────────────────────────

const EXT_INSTALLER = ['.exe', '.msi', '.msix', '.msixbundle', '.appx', '.appxbundle', '.pkg', '.run', '.sh', '.bin'];
const EXT_ARCHIVE   = ['.zip', '.7z', '.rar', '.tar.gz', '.tgz', '.tar.bz2', '.tar.xz', '.tar.zst', '.gz'];
const EXT_LINUX     = ['.deb', '.rpm', '.appimage', '.flatpakref', '.snap'];
const EXT_MOBILE    = ['.apk', '.xapk', '.ipa', '.aab'];
const EXT_MACOS     = ['.dmg'];
const EXT_ISO       = ['.iso', '.img', '.bin', '.nrg', '.mdf', '.vhd', '.vhdx', '.vmdk'];
const EXT_IMAGE     = ['.jpg', '.jpeg', '.png', '.gif', '.bmp', '.webp', '.svg', '.ico', '.tiff', '.tif', '.avif', '.heic', '.raw', '.cr2', '.nef', '.psd'];
const EXT_DOCUMENT  = ['.pdf', '.txt', '.csv', '.doc', '.docx', '.xls', '.xlsx', '.ppt', '.pptx', '.odt', '.ods', '.odp', '.rtf', '.epub', '.mobi'];
const EXT_FONT      = ['.ttf', '.otf', '.woff', '.woff2', '.eot'];
const EXT_MEDIA     = ['.mp3', '.mp4', '.wav', '.flac', '.ogg', '.aac', '.m4a', '.opus', '.mkv', '.avi', '.mov', '.wmv', '.webm', '.m4v'];
const EXT_CODE      = ['.py', '.js', '.ts', '.jar', '.war', '.ear', '.nupkg', '.vsix'];

// All extensions that are definitely a direct file, not a page
const ALL_DIRECT_EXTS = [
  ...EXT_INSTALLER, ...EXT_ARCHIVE, ...EXT_LINUX, ...EXT_MOBILE, ...EXT_MACOS,
  ...EXT_ISO, ...EXT_IMAGE, ...EXT_DOCUMENT, ...EXT_FONT, ...EXT_MEDIA, ...EXT_CODE
];

// URL patterns that are known to be direct CDN download links (not page redirects)
const CDN_PATTERNS = [
  'releases/download/',          // GitHub releases
  'api/releases/assets/',        // GitHub API asset
  'download.mozilla.org',        // Firefox
  'get.videolan.org',            // VLC
  'dl.google.com',               // Google
  'dl.pstmn.io',                 // Postman
  'cdn.akamai.steamstatic.com',  // Steam
  'updates.signal.org',          // Signal
  'vault.bitwarden.com/download',
  'discord.com/api/downloads',
  'laptop-updates.brave.com',
  'download.techpowerup.com',
  'downloads.sourceforge.net',
  'master.dl.sourceforge.net',
  'netix.dl.sourceforge.net',
  'cfhcable.dl.sourceforge.net',
  'download.gimp.org',
  'download.kde.org',
  'ftp.mozilla.org',
  'objects.githubusercontent.com', // GitHub release assets
  'github-releases.githubusercontent.com',
  'releases.hashicorp.com',
  'download.jetbrains.com',
  'download.oracle.com',
  'nodejs.org/dist/',
  'registry.npmjs.org',
  'pypi.org/packages/',
  'dl.discordapp.net',
  'cdn.discordapp.com',
  'download.docker.com',
  'update.code.visualstudio.com',
];

// ─── Classifier ───────────────────────────────────────────────────────────────

interface DownloadOption {
  url: string;
  os: string;
  arch: string;
  type: string;
  version?: string;
  isLatest: boolean;
  confidence: number;
  label?: string;
}

function classifyDownloadLink(url: string, linkText: string): DownloadOption {
  const lowerUrl = url.toLowerCase();
  const combined = lowerUrl + ' ' + linkText.toLowerCase();
  // strip query string for extension matching
  const urlNoQuery = lowerUrl.split('?')[0];

  // ── Category / type
  let type = 'File';
  if (EXT_INSTALLER.some(e => urlNoQuery.endsWith(e))) type = urlNoQuery.endsWith('.msi') ? 'MSI' : urlNoQuery.endsWith('.msix') || urlNoQuery.endsWith('.msixbundle') ? 'MSIX' : 'Installer';
  else if (EXT_ARCHIVE.some(e => urlNoQuery.endsWith(e)))  type = 'Archive';
  else if (EXT_ISO.some(e => urlNoQuery.endsWith(e)))      type = 'ISO/Image';
  else if (EXT_LINUX.some(e => urlNoQuery.endsWith(e)))    type = urlNoQuery.endsWith('.deb') ? 'DEB' : urlNoQuery.endsWith('.rpm') ? 'RPM' : urlNoQuery.endsWith('.appimage') ? 'AppImage' : 'Linux';
  else if (EXT_MOBILE.some(e => urlNoQuery.endsWith(e)))   type = urlNoQuery.endsWith('.apk') || urlNoQuery.endsWith('.xapk') ? 'APK' : 'IPA';
  else if (EXT_MACOS.some(e => urlNoQuery.endsWith(e)))    type = 'DMG';
  else if (EXT_IMAGE.some(e => urlNoQuery.endsWith(e)))    type = 'Image';
  else if (EXT_DOCUMENT.some(e => urlNoQuery.endsWith(e))) type = urlNoQuery.endsWith('.pdf') ? 'PDF' : 'Document';
  else if (EXT_FONT.some(e => urlNoQuery.endsWith(e)))     type = 'Font';
  else if (EXT_MEDIA.some(e => urlNoQuery.endsWith(e)))    type = 'Media';
  else if (EXT_CODE.some(e => urlNoQuery.endsWith(e)))     type = 'Package';
  else if (combined.includes('portable') || combined.includes('standalone')) type = 'Portable';

  // ── OS — only set a specific OS when the file type implies it OR the URL hints at it.
  //         Images, docs, fonts, media, generic archives → 'Any'
  const urlHintsMac     = combined.match(/\bmac\b|darwin|osx|macos/) != null;
  const urlHintsLinux   = combined.match(/\blinux\b|ubuntu|debian|fedora|centos|arch\b/) != null;
  const urlHintsWindows = combined.match(/\bwin(dows|32|64)?\b|\bx64\b.*setup|\bsetup\b.*x64/) != null;

  let os = 'Any'; // default — only override when we have a real signal

  if (EXT_MACOS.some(e => urlNoQuery.endsWith(e)) || urlHintsMac) {
    os = 'macOS';
  } else if (EXT_LINUX.some(e => urlNoQuery.endsWith(e)) || urlHintsLinux) {
    os = 'Linux';
  } else if (EXT_MOBILE.some(e => urlNoQuery.endsWith(e))) {
    os = (combined.match(/\bios\b|iphone|ipad/) || urlNoQuery.endsWith('.ipa')) ? 'iOS' : 'Android';
  } else if (EXT_INSTALLER.some(e => urlNoQuery.endsWith(e))) {
    // Installers: only call it Windows if the extension is Windows-only OR URL says so
    const winOnly = ['.exe','.msi','.msix','.msixbundle','.appx','.appxbundle'].some(e => urlNoQuery.endsWith(e));
    os = (winOnly || urlHintsWindows) ? 'Windows' : 'Any';
  } else if (EXT_ARCHIVE.some(e => urlNoQuery.endsWith(e)) || EXT_ISO.some(e => urlNoQuery.endsWith(e)) || EXT_CODE.some(e => urlNoQuery.endsWith(e))) {
    // Archives/ISOs/packages: use URL hints, otherwise Any
    if (urlHintsMac)     os = 'macOS';
    else if (urlHintsLinux)   os = 'Linux';
    else if (urlHintsWindows) os = 'Windows';
    else os = 'Any';
  }
  // Images, documents, fonts, media — always 'Any' (no OS override needed)

  // ── Arch
  let arch = '';
  if      (combined.match(/arm64|aarch64|arm-64/)) arch = 'ARM64';
  else if (combined.match(/\barm\b|armhf/))        arch = 'ARM';
  else if (combined.match(/x86_64|amd64|win64|\bx64\b|64bit|64-bit/)) arch = 'x64';
  else if (combined.match(/\bx86\b|32bit|win32|i386|i686|32-bit/))    arch = 'x86';

  // ── Version
  let version: string | undefined;
  const vm = url.match(/v?(\d+\.\d+(?:\.\d+)?(?:\.\d+)?)/);
  if (vm) version = vm[1];

  // ── Confidence
  let confidence = 0.55;
  if ([...EXT_INSTALLER, ...EXT_MACOS, ...EXT_LINUX, ...EXT_MOBILE, ...EXT_ISO].some(e => urlNoQuery.endsWith(e))) confidence = 0.90;
  else if ([...EXT_ARCHIVE, ...EXT_CODE].some(e => urlNoQuery.endsWith(e))) confidence = 0.75;
  else if ([...EXT_IMAGE, ...EXT_DOCUMENT, ...EXT_FONT, ...EXT_MEDIA].some(e => urlNoQuery.endsWith(e))) confidence = 0.80;
  if (CDN_PATTERNS.some(p => lowerUrl.includes(p))) confidence = Math.min(confidence + 0.08, 1.0);
  if (arch) confidence = Math.min(confidence + 0.05, 1.0);

  const isLatest = combined.includes('latest') || combined.includes('stable') || combined.includes('current');

  // For Any-OS files, show filename as the label; for platform files show OS + arch + type
  let label: string;
  if (os === 'Any') {
    const fname = url.split('/').pop()?.split('?')[0] || '';
    label = fname || type;
  } else {
    const parts = [os !== 'Windows' ? os : '', arch, type].filter(Boolean);
    label = parts.join(' ') || type;
  }

  return { url, os, arch, type, version, isLatest, confidence, label };
}

// ─── Scraper ──────────────────────────────────────────────────────────────────

function isDirectFileUrl(href: string): boolean {
  const lower = href.toLowerCase().split('?')[0];
  return ALL_DIRECT_EXTS.some(ext => lower.endsWith(ext));
}

function isTrustedCdnUrl(href: string): boolean {
  const lower = href.toLowerCase();
  return CDN_PATTERNS.some(p => lower.includes(p));
}

async function findDirectDownloadLinks(pageUrl: string, depth = 0): Promise<DownloadOption[]> {
  if (depth > 1) return []; // max one level of following — and only for known safe patterns

  try {
    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), 5000);
    const response = await fetch(pageUrl, {
      headers: {
        'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/124.0.0.0 Safari/537.36',
        'Accept': 'text/html,application/xhtml+xml,*/*;q=0.9',
        'Accept-Language': 'en-US,en;q=0.5'
      },
      signal: controller.signal,
      redirect: 'follow'
    });
    clearTimeout(timeoutId);

    // If the response itself is a file, return it directly
    const contentType = response.headers.get('content-type') || '';
    const contentDisposition = response.headers.get('content-disposition') || '';
    const isBinaryContent = [
      'application/octet-stream', 'application/x-msdownload', 'application/x-msi',
      'application/zip', 'application/x-7z-compressed', 'application/x-rar-compressed',
      'application/x-apple-diskimage', 'application/x-iso9660-image',
      'application/vnd.android.package-archive', 'application/pdf',
      'image/', 'audio/', 'video/', 'font/'
    ].some(t => contentType.includes(t));
    if (isBinaryContent || contentDisposition.includes('attachment')) {
      const fname = contentDisposition.match(/filename[^;=\n]*=["']?([^"'\n;]+)/i)?.[1]
                 || response.url.split('/').pop()?.split('?')[0]
                 || response.url;
      return [{ ...classifyDownloadLink(response.url, fname), label: fname }];
    }

    const html = await response.text();
    const $ = cheerio.load(html);
    const baseUrl = response.url;
    const baseHost = new URL(baseUrl).hostname.replace(/^www\./, '');

    const links: DownloadOption[] = [];
    const seen = new Set<string>();

    // Domains that are definitely NOT download sources (social, search, general web)
    const NON_DOWNLOAD_DOMAINS = [
      'facebook.com', 'twitter.com', 'x.com', 'instagram.com', 'linkedin.com',
      'reddit.com', 'youtube.com', 'tiktok.com', 'pinterest.com',
      'google.com', 'bing.com', 'yahoo.com', 'duckduckgo.com',
      'wikipedia.org', 'amazon.com', 'ebay.com',
    ];

    const addLink = (href: string, text: string): boolean => {
      if (!href || href.startsWith('javascript:') || href.startsWith('#') || href.startsWith('mailto:') || href.startsWith('tel:')) return false;
      try {
        const full = new URL(href, baseUrl).href;
        if (seen.has(full)) return true;

        // Skip links to non-download domains
        const linkHost = new URL(full).hostname.replace(/^www\./, '');
        if (NON_DOWNLOAD_DOMAINS.some(d => linkHost.includes(d))) return false;

        if (isDirectFileUrl(full) || isTrustedCdnUrl(full)) {
          // Skip tiny site assets: favicons, icons, logos, tracking pixels
          const fname = full.split('/').pop()?.split('?')[0]?.toLowerCase() || '';
          if (/^(favicon|icon|logo|pixel|tracker|badge|button|banner|sprite|thumb)/i.test(fname)) return false;
          // Skip image files that are likely page assets, not downloads
          const isImage = EXT_IMAGE.some(e => fname.endsWith(e));
          if (isImage) {
            // Only include images if link text suggests it's a download
            const ltext = text.toLowerCase();
            if (!ltext.includes('download') && !ltext.includes('save') && !ltext.includes('get')) return false;
          }

          seen.add(full);
          links.push(classifyDownloadLink(full, text));
          return true;
        }
      } catch {}
      return false;
    };

    // ── Scan all <a> tags — prioritize links whose text suggests a download
    $('a[href]').each((_i, el) => {
      const href = $(el).attr('href') || '';
      const text = $(el).text().trim();
      addLink(href, text);
    });

    // ── data-url / data-href attributes (some download buttons use these)
    $('[data-url],[data-href],[data-download]').each((_i, el) => {
      const u = $(el).attr('data-url') || $(el).attr('data-href') || $(el).attr('data-download') || '';
      addLink(u, $(el).text().trim());
    });

    // ── Regex scan raw HTML for file URLs that might be in JS/JSON
    const rawFileRegex = /["'](https?:\/\/[^"'<>\s]+?(?:\.exe|\.msi|\.msix|\.dmg|\.pkg|\.deb|\.rpm|\.appimage|\.apk|\.ipa|\.zip|\.7z|\.tar\.gz|\.iso|\.img|\.pdf|\.docx?|\.xlsx?|\.mp4|\.mp3|\.flac|\.ttf|\.otf|\.woff2?)(?:\?[^"'<>\s]*)?)["']/gi;
    let m: RegExpExecArray | null;
    while ((m = rawFileRegex.exec(html)) !== null) {
      addLink(m[1], '');
    }

    // ── Follow GitHub repo pages to their releases page
    if (links.length === 0 && depth === 0) {
      const ghMatch = baseUrl.match(/^https?:\/\/github\.com\/([^\/]+\/[^\/]+)\/?$/);
      if (ghMatch) {
        const releasesUrl = `https://github.com/${ghMatch[1]}/releases/latest`;
        const nested = await findDirectDownloadLinks(releasesUrl, depth + 1);
        links.push(...nested.filter(l => !seen.has(l.url)));
        nested.forEach(l => seen.add(l.url));
      }
    }

    // ── meta refresh — follow ONLY if it looks like a real file redirect (SourceForge etc.)
    if (links.length === 0 && depth === 0) {
      const metaRefresh = $('meta[http-equiv="refresh"]').attr('content');
      if (metaRefresh) {
        const urlPart = metaRefresh.split(/url=/i)[1]?.replace(/['"]/g, '').trim();
        if (urlPart) {
          try {
            const refreshFull = new URL(urlPart, baseUrl).href;
            // Only follow if the refresh target looks like a file or trusted CDN
            if (isDirectFileUrl(refreshFull) || isTrustedCdnUrl(refreshFull)) {
              addLink(refreshFull, '');
            } else if (refreshFull.includes('sourceforge.net') || refreshFull.includes('mirror') || refreshFull.includes('dl.')) {
              // Follow one level for known mirror patterns only
              const nested = await findDirectDownloadLinks(refreshFull, depth + 1);
              links.push(...nested.filter(l => !seen.has(l.url)));
              nested.forEach(l => seen.add(l.url));
            }
          } catch {}
        }
      }
    }

    if (links.length === 0) return [];

    // Filter out low-confidence results
    const MIN_CONFIDENCE = 0.65;
    const filtered = links.filter(l => l.confidence >= MIN_CONFIDENCE);
    if (filtered.length === 0) return [];

    return Array.from(new Map(filtered.map(l => [l.url, l])).values())
      .sort((a, b) => b.confidence - a.confidence)
      .slice(0, 15); // cap at 15 results

  } catch {
    return [];
  }
}

// ─── Endpoint ─────────────────────────────────────────────────────────────────

app.post('/api/get-download-link', async (req, res) => {
  try {
    const { url, title } = req.body as { url?: string; title?: string };
    if (!url) return res.json({ downloadLinks: [] });

    const lowerUrl = url.toLowerCase();
    const lowerTitle = (title || '').toLowerCase();

    // Fast-path hardcoded popular apps — label overrides the auto-generated one
    const mk = (dlUrl: string, label: string) => ({ ...classifyDownloadLink(dlUrl, label), label });
    if (lowerUrl.includes('discord.com')) return res.json({ downloadLinks: [
      mk('https://discord.com/api/download?platform=win', 'Windows x64'),
      mk('https://discord.com/api/download?platform=win32', 'Windows x86'),
      mk('https://discord.com/api/download?platform=linux&format=deb', 'Linux .deb'),
      mk('https://discord.com/api/download?platform=linux&format=tar.gz', 'Linux .tar.gz'),
    ]});
    if (lowerUrl.includes('google.com/chrome')) return res.json({ downloadLinks: [
      mk('https://dl.google.com/chrome/install/latest/chrome_installer.exe', 'Windows x64 Installer'),
      mk('https://dl.google.com/chrome/install/ChromeStandaloneSetup64.exe', 'Windows x64 Standalone'),
      mk('https://dl.google.com/chrome/install/ChromeStandaloneSetup.exe', 'Windows x86 Standalone'),
    ]});
    if (lowerUrl.includes('mozilla.org') && (lowerUrl.includes('firefox') || lowerTitle.includes('firefox'))) return res.json({ downloadLinks: [
      mk('https://download.mozilla.org/?product=firefox-latest-ssl&os=win64&lang=en-US', 'Windows x64'),
      mk('https://download.mozilla.org/?product=firefox-latest-ssl&os=win&lang=en-US', 'Windows x86'),
      mk('https://download.mozilla.org/?product=firefox-esr-latest-ssl&os=win64&lang=en-US', 'Windows x64 ESR'),
    ]});
    if (lowerUrl.includes('steampowered.com')) return res.json({ downloadLinks: [mk('https://cdn.akamai.steamstatic.com/client/installer/SteamSetup.exe', 'Windows Installer')] });
    if (lowerUrl.includes('spotify.com')) return res.json({ downloadLinks: [mk('https://download.scdn.co/SpotifySetup.exe', 'Windows Installer')] });
    if (lowerUrl.includes('code.visualstudio.com')) return res.json({ downloadLinks: [
      mk('https://code.visualstudio.com/sha/download?build=stable&os=win32-x64-user', 'Windows x64 User Installer'),
      mk('https://code.visualstudio.com/sha/download?build=stable&os=win32-x64', 'Windows x64 System Installer'),
      mk('https://code.visualstudio.com/sha/download?build=stable&os=win32-x64-archive', 'Windows x64 Portable .zip'),
      mk('https://code.visualstudio.com/sha/download?build=stable&os=win32-arm64-user', 'Windows ARM64'),
    ]});
    if (lowerUrl.includes('brave.com')) return res.json({ downloadLinks: [mk('https://laptop-updates.brave.com/latest/winx64', 'Windows x64')] });
    if (lowerUrl.includes('telegram.org')) return res.json({ downloadLinks: [
      mk('https://telegram.org/dl/desktop/win64', 'Windows x64'),
      mk('https://telegram.org/dl/desktop/win', 'Windows x86'),
      mk('https://telegram.org/dl/desktop/win64_portable', 'Windows x64 Portable'),
    ]});
    if (lowerUrl.includes('whatsapp.com')) return res.json({ downloadLinks: [mk('https://web.whatsapp.com/desktop/windows/release/x64/WhatsAppSetup.exe', 'Windows x64')] });
    if (lowerUrl.includes('zoom.us')) return res.json({ downloadLinks: [
      mk('https://zoom.us/client/latest/ZoomInstaller.exe', 'Windows Installer'),
      mk('https://zoom.us/client/latest/ZoomInstallerFull.exe?archType=x64', 'Windows x64 Full'),
    ]});
    if (lowerUrl.includes('epicgames.com')) return res.json({ downloadLinks: [mk('https://launcher-public-service-prod06.ol.epicgames.com/launcher/api/installer/download/EpicGamesLauncherInstaller.msi', 'Windows Installer')] });
    if (lowerUrl.includes('obsproject.com')) return res.json({ downloadLinks: [
      mk('https://cdn-fastly.obsproject.com/downloads/OBS-Studio-30.0.2-Full-Installer-x64.exe', 'Windows x64 Installer'),
      mk('https://cdn-fastly.obsproject.com/downloads/OBS-Studio-30.0.2-Full-Installer.exe', 'Windows x86 Installer'),
    ]});
    if (lowerUrl.includes('videolan.org')) return res.json({ downloadLinks: [
      mk('https://get.videolan.org/vlc/last/win64/', 'Windows x64'),
      mk('https://get.videolan.org/vlc/last/win32/', 'Windows x86'),
    ]});
    if (lowerUrl.includes('winrar.com') || lowerUrl.includes('rarlab.com')) return res.json({ downloadLinks: [
      mk('https://www.rarlab.com/rar/winrar-x64-701.exe', 'Windows x64'),
      mk('https://www.rarlab.com/rar/wrar701.exe', 'Windows x86'),
    ]});
    if (lowerUrl.includes('7-zip.org')) return res.json({ downloadLinks: [
      mk('https://www.7-zip.org/a/7z2407-x64.exe', 'Windows x64 Installer'),
      mk('https://www.7-zip.org/a/7z2407.exe', 'Windows x86 Installer'),
      mk('https://www.7-zip.org/a/7z2407-x64.msi', 'Windows x64 MSI'),
      mk('https://www.7-zip.org/a/7z2407-arm64.exe', 'Windows ARM64'),
    ]});
    if (lowerUrl.includes('notepad-plus-plus.org')) return res.json({ downloadLinks: [
      mk('https://github.com/notepad-plus-plus/notepad-plus-plus/releases/latest/download/npp.Installer.x64.exe', 'Windows x64 Installer'),
      mk('https://github.com/notepad-plus-plus/notepad-plus-plus/releases/latest/download/npp.Installer.exe', 'Windows x86 Installer'),
      mk('https://github.com/notepad-plus-plus/notepad-plus-plus/releases/latest/download/npp.portable.x64.zip', 'Windows x64 Portable'),
    ]});
    if (lowerUrl.includes('blender.org')) return res.json({ downloadLinks: [mk('https://www.blender.org/download/release/Blender4.0/blender-4.0.2-windows-x64.msi', 'Windows x64 MSI')] });
    if (lowerUrl.includes('gimp.org')) return res.json({ downloadLinks: [mk('https://download.gimp.org/gimp/v2.10/windows/gimp-2.10.36-setup.exe', 'Windows Installer')] });
    if (lowerUrl.includes('audacityteam.org')) return res.json({ downloadLinks: [
      mk('https://github.com/audacity/audacity/releases/latest/download/audacity-win-x64.exe', 'Windows x64'),
      mk('https://github.com/audacity/audacity/releases/latest/download/audacity-win-x86.exe', 'Windows x86'),
    ]});
    if (lowerUrl.includes('putty.org') || lowerUrl.includes('chiark.greenend.org.uk')) return res.json({ downloadLinks: [
      mk('https://the.earth.li/~sgtatham/putty/latest/w64/putty-64bit-installer.msi', 'Windows x64 MSI'),
      mk('https://the.earth.li/~sgtatham/putty/latest/w32/putty-installer.msi', 'Windows x86 MSI'),
      mk('https://the.earth.li/~sgtatham/putty/latest/w64/putty.exe', 'Windows x64 Portable .exe'),
    ]});
    if (lowerUrl.includes('filezilla-project.org')) return res.json({ downloadLinks: [mk('https://download.filezilla-project.org/client/FileZilla_Latest_win64-setup.exe', 'Windows x64')] });
    if (lowerUrl.includes('qbittorrent.org')) return res.json({ downloadLinks: [
      mk('https://github.com/qbittorrent/qBittorrent/releases/latest/download/qbittorrent_windows_x64.exe', 'Windows x64'),
      mk('https://github.com/qbittorrent/qBittorrent/releases/latest/download/qbittorrent_windows_x86.exe', 'Windows x86'),
    ]});
    if (lowerUrl.includes('handbrake.fr')) return res.json({ downloadLinks: [mk('https://github.com/HandBrake/HandBrake/releases/latest/download/HandBrake-Win_GUI.exe', 'Windows Installer')] });
    if (lowerUrl.includes('keepass.info')) return res.json({ downloadLinks: [mk('https://downloads.sourceforge.net/keepass/KeePass-Latest-Setup.exe', 'Windows Installer')] });
    if (lowerUrl.includes('bitwarden.com')) return res.json({ downloadLinks: [mk('https://vault.bitwarden.com/download/?app=desktop&platform=windows', 'Windows Installer')] });
    if (lowerUrl.includes('winmerge.org')) return res.json({ downloadLinks: [
      mk('https://downloads.sourceforge.net/winmerge/WinMerge-Latest-Setup.exe', 'Windows Installer'),
      mk('https://downloads.sourceforge.net/winmerge/WinMerge-Latest-x64-Setup.exe', 'Windows x64 Installer'),
    ]});
    if (lowerUrl.includes('irfanview.com')) return res.json({ downloadLinks: [
      mk('https://www.irfanview.com/main_download_engl.htm', 'Windows x64 Installer'),
    ]});
    if (lowerUrl.includes('inkscape.org')) return res.json({ downloadLinks: [
      mk('https://inkscape.org/release/inkscape-latest/windows/64-bit/msi/', 'Windows x64 MSI'),
      mk('https://inkscape.org/release/inkscape-latest/windows/64-bit/exe/', 'Windows x64 Installer'),
    ]});
    if (lowerUrl.includes('libreoffice.org')) return res.json({ downloadLinks: [
      mk('https://download.documentfoundation.org/libreoffice/stable/latest/win/x86_64/LibreOffice_latest_Win_x86-64.msi', 'Windows x64 MSI'),
      mk('https://download.documentfoundation.org/libreoffice/stable/latest/win/x86/LibreOffice_latest_Win_x86.msi', 'Windows x86 MSI'),
      mk('https://download.documentfoundation.org/libreoffice/stable/latest/mac/x86_64/LibreOffice_latest_MacOS_x86-64.dmg', 'macOS x64 DMG'),
      mk('https://download.documentfoundation.org/libreoffice/stable/latest/mac/aarch64/LibreOffice_latest_MacOS_aarch64.dmg', 'macOS ARM64 DMG'),
    ]});
    if (lowerUrl.includes('kdenlive.org')) return res.json({ downloadLinks: [
      mk('https://download.kde.org/stable/kdenlive/latest/win64/kdenlive-latest-amd64.exe', 'Windows x64'),
      mk('https://download.kde.org/stable/kdenlive/latest/macos/kdenlive-latest.dmg', 'macOS DMG'),
    ]});
    if (lowerUrl.includes('krita.org')) return res.json({ downloadLinks: [
      mk('https://download.kde.org/stable/krita/latest/krita-latest-x86_64.exe', 'Windows x64 Installer'),
      mk('https://download.kde.org/stable/krita/latest/krita-latest-x86_64.zip', 'Windows x64 Portable'),
      mk('https://download.kde.org/stable/krita/latest/krita-latest.dmg', 'macOS DMG'),
    ]});
    if (lowerUrl.includes('sumatrapdfreader.org')) return res.json({ downloadLinks: [
      mk('https://www.sumatrapdfreader.org/dl/rel/latest/SumatraPDF-latest-64-install.exe', 'Windows x64 Installer'),
      mk('https://www.sumatrapdfreader.org/dl/rel/latest/SumatraPDF-latest-64.zip', 'Windows x64 Portable'),
      mk('https://www.sumatrapdfreader.org/dl/rel/latest/SumatraPDF-latest-install.exe', 'Windows x86 Installer'),
    ]});
    if (lowerUrl.includes('mpc-hc.org') || lowerUrl.includes('github.com/clsid2/mpc-hc')) return res.json({ downloadLinks: [
      mk('https://github.com/clsid2/mpc-hc/releases/latest/download/MPC-HC.latest.x64.exe', 'Windows x64 Installer'),
      mk('https://github.com/clsid2/mpc-hc/releases/latest/download/MPC-HC.latest.x86.exe', 'Windows x86 Installer'),
    ]});
    if (lowerUrl.includes('potplayer.daum.net') || lowerUrl.includes('potplayerhome.net')) return res.json({ downloadLinks: [
      mk('https://t1.daumcdn.net/potplayer/PotPlayer/Version/Latest/PotPlayerSetup64.exe', 'Windows x64'),
      mk('https://t1.daumcdn.net/potplayer/PotPlayer/Version/Latest/PotPlayerSetup.exe', 'Windows x86'),
    ]});
    if (lowerUrl.includes('sublimetext.com')) return res.json({ downloadLinks: [
      mk('https://download.sublimetext.com/sublime_text_build_latest_x64_setup.exe', 'Windows x64 Installer'),
      mk('https://download.sublimetext.com/sublime_text_build_latest_x64.zip', 'Windows x64 Portable'),
      mk('https://download.sublimetext.com/sublime_text_build_latest_setup.exe', 'Windows x86 Installer'),
    ]});
    if (lowerUrl.includes('jetbrains.com')) return res.json({ downloadLinks: [
      mk('https://download.jetbrains.com/toolbox/jetbrains-toolbox-latest.exe', 'JetBrains Toolbox Windows'),
      mk('https://download.jetbrains.com/toolbox/jetbrains-toolbox-latest.dmg', 'JetBrains Toolbox macOS'),
    ]});
    if (lowerUrl.includes('virtualbox.org')) return res.json({ downloadLinks: [
      mk('https://download.virtualbox.org/virtualbox/LATEST-STABLE.TXT', 'Windows x64 Installer'),
    ]});
    if (lowerUrl.includes('ccleaner.com')) return res.json({ downloadLinks: [
      mk('https://download.ccleaner.com/ccsetup.exe', 'Windows Free Installer'),
      mk('https://download.ccleaner.com/CCleanerPortable.zip', 'Windows Free Portable'),
    ]});
    if (lowerUrl.includes('malwarebytes.com')) return res.json({ downloadLinks: [
      mk('https://downloads.malwarebytes.com/file/mb-windows', 'Windows Installer'),
      mk('https://downloads.malwarebytes.com/file/mb-mac', 'macOS PKG'),
    ]});
    if (lowerUrl.includes('teamviewer.com')) return res.json({ downloadLinks: [
      mk('https://download.teamviewer.com/download/TeamViewerSetup_x64.exe', 'Windows x64 Full'),
      mk('https://download.teamviewer.com/download/TeamViewerPortable.zip', 'Windows Portable'),
    ]});
    if (lowerUrl.includes('anydesk.com')) return res.json({ downloadLinks: [
      mk('https://download.anydesk.com/AnyDesk.exe', 'Windows Installer'),
    ]});
    if (lowerUrl.includes('rufus.ie')) return res.json({ downloadLinks: [
      mk('https://github.com/pbatard/rufus/releases/latest/download/rufus-latest.exe', 'Windows x64'),
      mk('https://github.com/pbatard/rufus/releases/latest/download/rufus-latest_arm64.exe', 'Windows ARM64'),
    ]});
    if (lowerUrl.includes('balena.io/etcher') || lowerUrl.includes('etcher.balena.io')) return res.json({ downloadLinks: [
      mk('https://github.com/balena-io/etcher/releases/latest/download/balenaEtcher-latest-x64.exe', 'Windows x64 Installer'),
      mk('https://github.com/balena-io/etcher/releases/latest/download/balenaEtcher-latest-x64-portable.exe', 'Windows x64 Portable'),
    ]});
    if (lowerUrl.includes('cpuid.com/softwares/cpu-z') || (lowerUrl.includes('cpuid.com') && lowerTitle.includes('cpu-z'))) return res.json({ downloadLinks: [
      mk('https://download.cpuid.com/cpu-z/cpu-z_latest_en.exe', 'Windows Installer'),
      mk('https://download.cpuid.com/cpu-z/cpu-z_latest_en.zip', 'Windows Portable'),
    ]});
    if (lowerUrl.includes('gpuz.techpowerup.com') || (lowerTitle.includes('gpu-z') && lowerUrl.includes('techpowerup'))) return res.json({ downloadLinks: [
      mk('https://download.techpowerup.com/files/GPU-Z.latest.exe', 'Windows Installer'),
    ]});
    if (lowerUrl.includes('crystaldiskinfo.sharkfest.jp') || (lowerTitle.includes('crystaldiskinfo'))) return res.json({ downloadLinks: [
      mk('https://crystalmark.info/redirect.php?product=CrystalDiskInfo&type=installer', 'Windows Installer'),
      mk('https://crystalmark.info/redirect.php?product=CrystalDiskInfo&type=portable', 'Windows Portable'),
    ]});
    if (lowerUrl.includes('hwinfo.com') || lowerTitle.includes('hwinfo')) return res.json({ downloadLinks: [
      mk('https://www.hwinfo.com/files/hwi_latest.exe', 'Windows Installer'),
      mk('https://www.hwinfo.com/files/hwi_latest.zip', 'Windows Portable'),
    ]});

    // Check learned downloads for this host (user-taught, highest priority after fast-path)
    try {
      const host = new URL(url).hostname.replace(/^www\./, '');
      const learned = learnedByHost.get(host) || [];
      if (learned.length > 0) {
        const learnedLinks = learned
          .sort((a, b) => b.count - a.count)
          .map(e => {
            const classified = classifyDownloadLink(e.downloadUrl, '');
            const fn = e.downloadUrl.split('/').pop()?.split('?')[0] || '';
            return { ...classified, label: `⭐ ${classified.label || fn} (you downloaded this)`, isLatest: true };
          });
        return res.json({ downloadLinks: learnedLinks });
      }
    } catch {}

    // Check cache first
    const cached = resultCache.get(url);
    if (cached && Date.now() - cached.ts < CACHE_TTL) {
      return res.json({ downloadLinks: cached.links });
    }

    // Scrape the page
    const downloadLinks = await findDirectDownloadLinks(url);

    // Store in cache; evict stale entries if cache grows large
    resultCache.set(url, { links: downloadLinks, ts: Date.now() });
    if (resultCache.size > 500) {
      const now = Date.now();
      for (const [k, v] of resultCache)
        if (now - v.ts > CACHE_TTL) resultCache.delete(k);
    }

    res.json({ downloadLinks });
  } catch (e) {
    console.error('Failed:', e);
    res.json({ downloadLinks: [] });
  }
});

// ─── Start ────────────────────────────────────────────────────────────────────

const PORT = 3210;
app.listen(PORT, '127.0.0.1', () => {
  console.log(`YCB SmartDL running on port ${PORT}`);
});

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
  res.setHeader('Access-Control-Allow-Headers', 'Content-Type');
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

// ─── Download Link Classifier ─────────────────────────────────────────────────

interface DownloadOption {
  url: string;
  os: string;
  arch: string;
  type: string;
  version?: string;
  isLatest: boolean;
  confidence: number;
  label?: string; // human-readable name shown in dropdown
}

function classifyDownloadLink(url: string, linkText: string): DownloadOption {
  const lowerUrl = url.toLowerCase();
  const lowerText = linkText.toLowerCase();
  const combined = lowerUrl + ' ' + lowerText;

  // OS detection
  let os = 'Windows';
  if (combined.includes('mac') || combined.includes('darwin') || combined.includes('.dmg') || combined.includes('osx') || combined.includes('macos')) os = 'macOS';
  else if (combined.includes('linux') || combined.includes('.deb') || combined.includes('.rpm') || combined.includes('.appimage') || combined.includes('.tar.gz')) os = 'Linux';
  else if (combined.includes('android') || combined.includes('.apk')) os = 'Android';
  else if (combined.includes('ios') || combined.includes('iphone') || combined.includes('ipad')) os = 'iOS';

  // Arch detection
  let arch = '';
  if (combined.includes('arm64') || combined.includes('aarch64') || combined.includes('arm-64')) arch = 'ARM64';
  else if (combined.includes('arm') || combined.includes('armhf')) arch = 'ARM';
  else if (combined.includes('x86_64') || combined.includes('amd64') || combined.includes('win64') || combined.includes('x64') || combined.includes('64bit') || combined.includes('64-bit')) arch = 'x64';
  else if (combined.includes('x86') || combined.includes('32bit') || combined.includes('win32') || combined.includes('i386') || combined.includes('i686') || combined.includes('32-bit')) arch = 'x86';

  // Type detection
  let type = '';
  if (combined.includes('portable') || combined.includes('standalone')) type = 'Portable';
  else if (lowerUrl.endsWith('.msi')) type = 'MSI';
  else if (lowerUrl.endsWith('.exe')) type = 'Installer';
  else if (lowerUrl.endsWith('.zip') || lowerUrl.endsWith('.7z')) type = 'Portable';
  else if (lowerUrl.endsWith('.msix') || lowerUrl.endsWith('.msixbundle')) type = 'MSIX';

  // Version extraction
  let version: string | undefined;
  const versionMatch = combined.match(/v?(\d+\.\d+(\.\d+)?(\.\d+)?)/);
  if (versionMatch) version = versionMatch[1];

  // Confidence score
  let confidence = 0.5;
  if (lowerUrl.endsWith('.exe') || lowerUrl.endsWith('.dmg') || lowerUrl.endsWith('.apk')) confidence += 0.2;
  if (lowerText.includes('download')) confidence += 0.1;
  if (arch) confidence += 0.1;

  const isLatest = combined.includes('latest') || combined.includes('stable') || combined.includes('current');

  // Build human-readable label from parts
  const parts = [os !== 'Windows' ? os : '', arch, type].filter(Boolean);
  const label = parts.length > 0 ? parts.join(' ') : (type || os);

  return { url, os, arch, type, version, isLatest, confidence, label };
}

async function findDirectDownloadLinks(pageUrl: string, depth = 0): Promise<DownloadOption[]> {
  if (depth > 2) return [];

  try {
    const controller = new AbortController();
    const timeoutId = setTimeout(() => controller.abort(), 4000);
    const response = await fetch(pageUrl, {
      headers: {
        'User-Agent': 'Mozilla/5.0 (Windows NT 10.0; Win64; x64) AppleWebKit/537.36 (KHTML, like Gecko) Chrome/120.0.0.0 Safari/537.36',
        'Accept': 'text/html,application/xhtml+xml,application/xml;q=0.9,image/webp,*/*;q=0.8',
        'Accept-Language': 'en-US,en;q=0.5'
      },
      signal: controller.signal,
      redirect: 'follow'
    });
    clearTimeout(timeoutId);

    const contentType = response.headers.get('content-type') || '';
    const contentDisposition = response.headers.get('content-disposition') || '';
    const directTypes = [
      'application/octet-stream', 'application/x-msdownload', 'application/x-msi',
      'application/zip', 'application/x-7z-compressed', 'application/x-rar-compressed',
      'application/x-apple-diskimage', 'application/x-iso9660-image',
      'application/vnd.android.package-archive',
    ];
    if (directTypes.some(t => contentType.includes(t)) || contentDisposition.includes('attachment')) {
      return [{ ...classifyDownloadLink(response.url, ''), label: response.url.split('/').pop()?.split('?')[0] || response.url }];
    }

    const html = await response.text();
    const $ = cheerio.load(html);

    const downloadExtensions = [
      '.exe', '.msi', '.msix', '.msixbundle', '.appx', '.appxbundle',
      '.dmg', '.pkg',
      '.deb', '.rpm', '.appimage', '.flatpak', '.snap',
      '.apk', '.xapk',
      '.zip', '.7z', '.rar', '.tar.gz', '.tgz', '.tar.bz2', '.tar.xz',
      '.iso', '.img', '.bin',
      '.run', '.sh'
    ];

    const downloadKeywords = [
      'api/download', 'releases/download', 'download?os=', 'get.videolan.org',
      'download.mozilla.org', '/dl/', '/download/', 'latest/download',
      'download_latest', '?download=true', '&download=true', 'download.php',
      'get-download', 'click-to-download', 'start-download', 'download-now',
      'mirrors', 'sourceforge.net/projects/', 'sourceforge.net/p/'
    ];

    const links: DownloadOption[] = [];
    let bestPageLink: string | null = null;
    let bestPageScore = -1;

    const addLink = (href: string, text: string) => {
      if (!href || href.startsWith('javascript:') || href.startsWith('#') || href.startsWith('mailto:')) return false;
      const lowerHref = href.toLowerCase();
      const isDirect = downloadExtensions.some(ext => lowerHref.split('?')[0].endsWith(ext)) ||
                       downloadKeywords.some(kw => lowerHref.includes(kw));
      if (isDirect) {
        try {
          const fullUrl = new URL(href, response.url).href;
          if (!links.some(l => l.url === fullUrl)) {
            links.push(classifyDownloadLink(fullUrl, text));
          }
        } catch (e) {}
        return true;
      }
      return false;
    };

    $('a').each((_i, el) => {
      const href = $(el).attr('href');
      const text = $(el).text().trim();
      if (!href) return;
      if (addLink(href, text)) return;

      const lowerHref = href.toLowerCase();
      const lowerText = text.toLowerCase();
      const className = ($(el).attr('class') || '').toLowerCase();
      const id = ($(el).attr('id') || '').toLowerCase();

      let score = 0;
      if (lowerText === 'download') score += 30;
      if (lowerText.includes('download for windows')) score += 50;
      if (lowerText.includes('download latest')) score += 40;
      if (lowerText.includes('get ')) score += 10;
      if (lowerHref.includes('download')) score += 20;
      if (lowerHref.includes('windows') || lowerHref.includes('win64')) score += 15;
      if (className.includes('download') || id.includes('download')) score += 15;
      if (className.includes('btn') || className.includes('button')) score += 5;

      if (score > bestPageScore && score >= 15) {
        bestPageScore = score;
        try { bestPageLink = new URL(href, response.url).href; } catch (e) {}
      }
    });

    $('[data-url], [data-href]').each((_i, el) => {
      const url = $(el).attr('data-url') || $(el).attr('data-href');
      const text = $(el).text().trim();
      if (url) addLink(url, text);
    });

    const urlRegex = /(?:https?:\/\/|^\/)[^\s'"]+?(?:\.exe|\.msi|\.dmg|\.zip|\.apk|\.pkg)(?:\?[^\s'"]*)?/gi;
    const matches = html.match(urlRegex);
    if (matches) {
      for (const match of matches) addLink(match, '');
    }

    const metaRefresh = $('meta[http-equiv="refresh"]').attr('content');
    if (metaRefresh) {
      const parts = metaRefresh.split(/url=/i);
      if (parts.length > 1) {
        const refreshUrl = parts[1].replace(/['"]/g, '').trim();
        addLink(refreshUrl, '');
        if (links.length === 0 && depth < 2) {
          try {
            const nestedLinks = await findDirectDownloadLinks(new URL(refreshUrl, response.url).href, depth + 1);
            links.push(...nestedLinks);
          } catch (e) {}
        }
      }
    }

    $('iframe').each((_i, el) => {
      const src = $(el).attr('src');
      if (src) addLink(src, '');
    });

    if (links.length > 0) {
      const uniqueLinks = Array.from(new Map(links.map(l => [l.url, l])).values());
      return uniqueLinks.sort((a, b) => b.confidence - a.confidence);
    }

    if (bestPageLink && depth < 2 && bestPageLink !== pageUrl && (bestPageLink as string).startsWith('http')) {
      return await findDirectDownloadLinks(bestPageLink, depth + 1);
    }

    return [];
  } catch (e) {
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

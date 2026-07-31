using System.Globalization;
using System.Windows.Markup;

namespace CaYaScreenBridge.Windows.Ui;

/// <summary>
/// A small two language string table.
///
/// A full resource assembly would be overkill for one window, and the tray menu has to be built in
/// code anyway, so a dictionary lookup that both XAML and C# can reach is the simplest thing that
/// covers the whole surface.
///
/// The interface follows the system language. Turkish is used when Windows is running in Turkish;
/// everything else falls back to English, including languages this application has no translation
/// for, so the interface is never a mix of the two.
/// </summary>
public static class Loc
{
    private static Dictionary<string, string> _active;

    static Loc()
    {
        // The tables are declared at the bottom of the file for readability, and static field
        // initialisers run in textual order, so an inline initialiser here would capture null.
        _active = English;
    }

    /// <summary>Two letter code of the language currently in use: "tr" or "en".</summary>
    public static string Language { get; private set; } = "en";

    /// <summary>
    /// Looks up a string, falling back to English and finally to the key itself. Returning the key
    /// rather than throwing means a missing entry shows up as an obviously wrong label instead of
    /// taking down the window it appears on.
    /// </summary>
    public static string Get(string key) =>
        _active.TryGetValue(key, out string? value) ? value :
        English.TryGetValue(key, out string? fallback) ? fallback : key;

    /// <summary>
    /// Applies a language setting: "auto" follows Windows, otherwise an explicit "tr" or "en".
    /// </summary>
    public static void Apply(string setting)
    {
        string language = setting;

        if (string.IsNullOrWhiteSpace(setting) || setting.Equals("auto", StringComparison.OrdinalIgnoreCase))
        {
            language = CultureInfo.CurrentUICulture.TwoLetterISOLanguageName;
        }

        Language = language.Equals("tr", StringComparison.OrdinalIgnoreCase) ? "tr" : "en";
        _active = Language == "tr" ? Turkish : English;
    }

    private static readonly Dictionary<string, string> Turkish = new(StringComparer.Ordinal)
    {
        ["app.name"] = "CaYaScreenBridge",
        ["app.tagline"] = "Farklı DPI'lı ekranlar arasında hizalı imleç",

        ["nav.overview"] = "Genel Bakış",
        ["nav.displays"] = "Ekranlar",
        ["nav.transition"] = "Geçiş",
        ["nav.drag"] = "Pencere Sürükleme",
        ["nav.rules"] = "Uygulama Kuralları",
        ["nav.startup"] = "Başlangıç",
        ["nav.diagnostics"] = "Tanılama",

        ["common.enabled"] = "Etkin",
        ["common.disabled"] = "Devre dışı",
        ["common.on"] = "Açık",
        ["common.off"] = "Kapalı",
        ["common.save"] = "Kaydet",
        ["common.reset"] = "Sıfırla",
        ["common.add"] = "Ekle",
        ["common.remove"] = "Kaldır",
        ["common.apply"] = "Uygula",
        ["common.close"] = "Kapat",
        ["common.mm"] = "mm",
        ["common.inch"] = "inç",

        ["overview.status"] = "Durum",
        ["overview.running"] = "Çalışıyor",
        ["overview.paused"] = "Duraklatıldı",
        ["overview.hook"] = "Sistem kancası",
        ["overview.rawinput"] = "Ham girdi desteği",
        ["overview.displays"] = "Algılanan ekran",
        ["overview.crossings"] = "Ekran geçişi",
        ["overview.recoveries"] = "Otomatik kurtarma",
        ["overview.restarts"] = "Kanca yeniden kurulumu",
        ["overview.master"] = "İmleç hizalamayı etkinleştir",
        ["overview.masterHint"] = "Kapatıldığında hiçbir imleç düzeltmesi yapılmaz; uygulama arka planda beklemede kalır.",
        ["overview.policy"] = "Etkin politika",
        ["overview.uniform"] = "Tüm ekranlar aynı piksel yoğunluğuna sahip; düzeltmeye gerek yok.",

        ["displays.title"] = "Ekran yerleşimi",
        ["displays.hint"] = "Ekranları gerçek masaüstü düzeninize göre sürükleyin. Konumlar milimetre cinsinden saklanır; imleç geçişleri bu fiziksel yerleşime göre hesaplanır.",
        ["displays.snap"] = "Kenarlara yapış",
        ["displays.autoLayout"] = "Windows düzeninden yeniden kur",
        ["displays.detected"] = "Algılanan",
        ["displays.size"] = "Panel boyutu",
        ["displays.position"] = "Konum",
        ["displays.diagonal"] = "Köşegen",
        ["displays.resolution"] = "Çözünürlük",
        ["displays.scale"] = "Ölçek",
        ["displays.density"] = "Yoğunluk",
        ["displays.edid"] = "EDID'den",
        ["displays.manual"] = "Elle girildi",
        ["displays.primary"] = "Birincil",
        ["displays.useEdid"] = "EDID değerine dön",
        ["displays.profile"] = "Etkin profil",
        ["calibration.open"] = "Düz çizgi kalibrasyonu",
        ["calibration.title"] = "Fiziksel yerleşim kalibrasyonu",
        ["calibration.hint"] = "Ekranlarda gösterilen kırmızı çizgileri fiziksel bir cetvel veya düz nesneyle hizalayın. Birincil ekran referanstır; seçilen ekranın fiziksel konumu milimetre adımlarıyla değiştirilir.",
        ["calibration.referenceHint"] = "Yatay çizgi ekranın dikey konumunu (Y), dikey çizgi yatay konumunu (X) kalibre eder. Birincil ekran sabit referans olarak tutulur.",
        ["calibration.target"] = "Ayarlanacak ekran",
        ["calibration.orientation"] = "Çizgi yönü",
        ["calibration.horizontal"] = "Yatay çizgi — dikey konumu ayarla",
        ["calibration.vertical"] = "Dikey çizgi — yatay konumu ayarla",
        ["calibration.both"] = "Yatay + dikey birlikte",
        ["calibration.dragHint"] = "Seçili ekranın köşelerindeki cetvelleri sürükleyin. Üst/alt cetveller Y konumunu, sol/sağ cetveller X konumunu değiştirir. Shift ince, Ctrl çok ince ayar yapar.",
        ["calibration.adjustX"] = "Yatay konum (X) ince ayarı",
        ["calibration.adjustY"] = "Dikey konum (Y) ince ayarı",
        ["calibration.precisionHint"] = "Ana çizgiler kalın, yardımcı çizgiler ince gösterilir. Birincil ekranın %25, %50 ve %75 fiziksel referansları tüm ekranlarda çizilir.",
        ["calibration.noTarget"] = "Ayarlanabilir bir hedef ekran bulunamadı.",

        ["transition.title"] = "Geçiş davranışı",
        ["transition.align"] = "Fiziksel hizalama",
        ["transition.alignHint"] = "İmleç, ekranlar arasında piksel satırını değil masadaki gerçek yüksekliğini korur.",
        ["transition.rawInput"] = "Kenar destekli geçiş (ham girdi)",
        ["transition.rawInputHint"] = "Windows imleci masaüstü kenarına sabitlediğinde hareketin devamını ham HID verisinden yeniden kurar. Fiziksel olarak komşu ama piksel olarak kaymış ekranlara geçişi güvenilir yapan şey budur.",
        ["transition.preventLoss"] = "İmleci asla kaybetme",
        ["transition.preventLossHint"] = "L biçimli yerleşimlerde boşluğa düşen imleci en yakın ekrana geri alır.",
        ["transition.speed"] = "İşaretçi hızını ekrana göre eşitle",
        ["transition.speedHint"] = "Aynı el hareketi her ekranda aynı fiziksel mesafeyi kat eder. Windows işaretçi hızı ayarını değiştirir, çıkışta geri alınır.",
        ["transition.resistance"] = "Kenar direnci",
        ["transition.resistanceHint"] = "Geçiş için kenara bastırılması gereken mesafe. 0 = direnç yok.",
        ["transition.speedAdaptive"] = "Hıza bağlı direnç",
        ["transition.speedAdaptiveHint"] = "Yavaş harekette tam direnci korur; hızlı ve bilinçli geçişlerde direnci kademeli azaltır.",
        ["transition.speedReference"] = "Azalmanın başladığı hız",
        ["transition.advancedResistance"] = "Ekran ve kenar başına gelişmiş direnç",
        ["transition.advancedResistanceHint"] = "Bir ekran ve kenar seçerek genel direnç değerini veya hıza bağlı davranışı geçersiz kılın.",
        ["transition.display"] = "Ekran",
        ["transition.edge"] = "Kenar",
        ["transition.custom"] = "Özel değer",
        ["transition.speedMode"] = "Hız davranışı",
        ["transition.inherit"] = "Geneli kullan",
        ["transition.edge.left"] = "Sol",
        ["transition.edge.top"] = "Üst",
        ["transition.edge.right"] = "Sağ",
        ["transition.edge.bottom"] = "Alt",
        ["transition.wrap"] = "Kenardan sarma",
        ["transition.wrap.none"] = "Kapalı",
        ["transition.wrap.horizontal"] = "Yatay",
        ["transition.wrap.vertical"] = "Dikey",
        ["transition.wrap.both"] = "Yatay ve dikey",

        ["drag.title"] = "Pencere sürükleme",
        ["drag.mode"] = "Ölçekleme modu",
        ["drag.mode.off"] = "Kapalı",
        ["drag.mode.drop"] = "Ölçülü — bırakınca düzelt",
        ["drag.mode.live"] = "Agresif — sürüklerken canlı",
        ["drag.modeHint"] = "Agresif modda pencere, DPI sınırını geçtiği anda gerçek boyutunu koruyacak şekilde yeniden ölçeklenir ve tuttuğunuz nokta imlecin altında kalır.",
        ["drag.grab"] = "Tutma noktasını koru",
        ["drag.seamless"] = "Ekranlar arası pencere sürekliliği",
        ["drag.seamlessHint"] = "Yalnızca pencere iki veya daha fazla ekranı aynı anda kaplarken geçiş görünümünü eşler. Pencere tek ekrana geçince tüm düzeltmeler durur ve boyutlandırmayı Windows devralır.",
        ["drag.seamlessWarningTitle"] = "Deneysel özellik",
        ["drag.seamlessWarning"] = "Bu özellik bazı uygulamalarda, özel pencere türlerinde veya farklı ekran/DPI düzenlerinde beklenen sonucu vermeyebilir ya da hiç çalışmayabilir. Sorun yaşarsanız kapalı bırakın.",
        ["drag.skipUnaware"] = "DPI farkında olmayan pencereleri atla",
        ["drag.skipUnawareHint"] = "Windows bu pencereleri bitmap olarak esnetir; dışarıdan yeniden boyutlandırmak bulanık sonuç verir.",
        ["drag.skipMaximised"] = "Büyütülmüş pencereleri atla",
        ["drag.throttle"] = "Canlı güncelleme aralığı",

        ["rules.title"] = "Uygulama kuralları",
        ["rules.hint"] = "Belirli uygulamalar ön plandayken davranışı değiştirin. Süreç adını uzantısız yazın; sonuna * koyarak ön ek eşleşmesi yapabilirsiniz.",
        ["rules.process"] = "Süreç adı",
        ["rules.action"] = "Davranış",
        ["rules.action.correct"] = "Normal düzeltme",
        ["rules.action.passthrough"] = "Hiç karışma",
        ["rules.action.noscale"] = "İmleci düzelt, pencere ölçekleme yok",
        ["rules.games"] = "Oyun ve tam ekran",
        ["rules.exclusive"] = "Özel tam ekranda duraklat",
        ["rules.exclusiveHint"] = "Direct3D özel tam ekran modunda imleç zaten tek ekrana kilitlidir; müdahale etmemek en güvenlisidir.",
        ["rules.borderless"] = "Kenarlıksız tam ekranda düzeltmeyi sürdür",
        ["rules.anticheat"] = "Anti-cheat çalışırken tamamen dur",
        ["rules.anticheatHint"] = "Çekirdek düzeyi anti-cheat yazılımları enjekte edilmiş imleç hareketini otomasyon olarak okuyabilir. Güvenli varsayılan yoldan çekilmektir.",
        ["rules.deepIntegration"] = "Gelişmiş Windows entegrasyonu",
        ["rules.deepIntegrationHint"] = "Yükseltilmiş uygulamalar, sanal masaüstleri ve masaüstü/oturum geçişleri için yönetici yetkisiyle daha sıkı kanca izleme ve otomatik kurtarma kullanır. Winlogon güvenli masaüstüne doğrudan erişim Windows tarafından sınırlandırılır.",

        ["startup.title"] = "Başlangıç ve güvenilirlik",
        ["startup.enable"] = "Windows ile birlikte başlat",
        ["startup.elevated"] = "Yönetici olarak başlat (Görev Zamanlayıcı)",
        ["startup.elevatedHint"] = "Standart kullanıcı olarak çalışan bir kanca, yükseltilmiş bir pencere öndeyken yok sayılır. Görev Zamanlayıcı görevi, her oturum açılışında UAC istemi göstermeden bu yetkiyi verir.",
        ["startup.minimised"] = "Simge durumunda başlat",
        ["startup.tray"] = "Sistem tepsisi simgesini göster",
        ["startup.hookTimeout"] = "Kanca zaman aşımını yükselt",
        ["startup.hookTimeoutHint"] = "Windows, geri çağırması gecikmeli olan düşük seviye kancaları haber vermeden kaldırır. Bu değeri yükseltmek o hata sınıfını ortadan kaldırır; oturum kapatıp açtıktan sonra geçerli olur.",
        ["startup.method"] = "Kayıt yöntemi",
        ["startup.method.task"] = "Zamanlanmış görev (yükseltilmiş)",
        ["startup.method.run"] = "Kayıt defteri Run anahtarı",
        ["startup.method.none"] = "Kayıtlı değil",
        ["startup.language"] = "Arayüz dili",
        ["startup.languageHint"] = "Değişiklik uygulama yeniden başlatıldığında geçerli olur.",
        ["startup.repair"] = "Kaydı onar",
        ["settings.title"] = "Ayarları yedekle ve taşı",
        ["settings.hint"] = "Tüm genel ayarlar, ekran profilleri, kalibrasyonlar, direnç seçenekleri ve uygulama kuralları tek JSON dosyasına dahil edilir.",
        ["settings.export"] = "Ayarları dışa aktar",
        ["settings.import"] = "Ayarları içe aktar",
        ["settings.fileFilter"] = "CaYaScreenBridge ayarları (*.json)|*.json|Tüm dosyalar (*.*)|*.*",
        ["settings.exportSuccess"] = "Ayarlar başarıyla dışa aktarıldı.",
        ["settings.exportFailed"] = "Ayarlar dışa aktarılamadı.",
        ["settings.importSuccess"] = "Ayarlar doğrulandı, kaydedildi ve canlı olarak uygulandı.",
        ["settings.importFailed"] = "Seçilen dosya içe aktarılamadı; mevcut ayarlar değiştirilmedi.",

        ["diag.title"] = "Tanılama",
        ["diag.copy"] = "Günlüğü kopyala",
        ["diag.openFolder"] = "Klasörü aç",
        ["diag.verbose"] = "Ayrıntılı günlük",
        ["diag.rebuild"] = "Yerleşimi yeniden kur",
        ["diag.rearm"] = "Kancayı yeniden kur",
        ["diag.live"] = "Canlı imleç",

        ["tray.show"] = "Ayarları aç",
        ["tray.pause"] = "Düzeltmeyi duraklat",
        ["tray.resume"] = "Düzeltmeyi sürdür",
        ["tray.rebuild"] = "Yerleşimi yeniden kur",
        ["tray.exit"] = "Çıkış",
        ["tray.tipRunning"] = "CaYaScreenBridge — etkin",
        ["tray.tipPaused"] = "CaYaScreenBridge — duraklatıldı",
    };

    private static readonly Dictionary<string, string> English = new(StringComparer.Ordinal)
    {
        ["app.name"] = "CaYaScreenBridge",
        ["app.tagline"] = "Aligned cursor across mixed DPI displays",

        ["nav.overview"] = "Overview",
        ["nav.displays"] = "Displays",
        ["nav.transition"] = "Transition",
        ["nav.drag"] = "Window dragging",
        ["nav.rules"] = "Application rules",
        ["nav.startup"] = "Startup",
        ["nav.diagnostics"] = "Diagnostics",

        ["common.enabled"] = "Enabled",
        ["common.disabled"] = "Disabled",
        ["common.on"] = "On",
        ["common.off"] = "Off",
        ["common.save"] = "Save",
        ["common.reset"] = "Reset",
        ["common.add"] = "Add",
        ["common.remove"] = "Remove",
        ["common.apply"] = "Apply",
        ["common.close"] = "Close",
        ["common.mm"] = "mm",
        ["common.inch"] = "in",

        ["overview.status"] = "Status",
        ["overview.running"] = "Running",
        ["overview.paused"] = "Paused",
        ["overview.hook"] = "System hook",
        ["overview.rawinput"] = "Raw input assist",
        ["overview.displays"] = "Displays detected",
        ["overview.crossings"] = "Screen crossings",
        ["overview.recoveries"] = "Automatic recoveries",
        ["overview.restarts"] = "Hook reinstalls",
        ["overview.master"] = "Enable cursor alignment",
        ["overview.masterHint"] = "With this off no cursor correction is applied; the application stays idle in the background.",
        ["overview.policy"] = "Active policy",
        ["overview.uniform"] = "Every display has the same pixel density, so no correction is needed.",

        ["displays.title"] = "Display layout",
        ["displays.hint"] = "Drag the displays to match your real desk. Positions are stored in millimetres, and cursor transitions are computed from this physical layout.",
        ["displays.snap"] = "Snap to edges",
        ["displays.autoLayout"] = "Rebuild from the Windows layout",
        ["displays.detected"] = "Detected",
        ["displays.size"] = "Panel size",
        ["displays.position"] = "Position",
        ["displays.diagonal"] = "Diagonal",
        ["displays.resolution"] = "Resolution",
        ["displays.scale"] = "Scale",
        ["displays.density"] = "Density",
        ["displays.edid"] = "From EDID",
        ["displays.manual"] = "Entered manually",
        ["displays.primary"] = "Primary",
        ["displays.useEdid"] = "Revert to the EDID value",
        ["displays.profile"] = "Active profile",
        ["calibration.open"] = "Straight-line calibration",
        ["calibration.title"] = "Physical layout calibration",
        ["calibration.hint"] = "Align the red lines shown across the displays with a physical ruler or another straight object. The primary display is the reference; the selected display's physical position is adjusted in millimetres.",
        ["calibration.referenceHint"] = "A horizontal line calibrates vertical position (Y); a vertical line calibrates horizontal position (X). The primary display remains the fixed reference.",
        ["calibration.target"] = "Display to adjust",
        ["calibration.orientation"] = "Line orientation",
        ["calibration.horizontal"] = "Horizontal line — adjust vertical position",
        ["calibration.vertical"] = "Vertical line — adjust horizontal position",
        ["calibration.both"] = "Horizontal + vertical together",
        ["calibration.dragHint"] = "Drag the rulers at the corners of the selected display. Top/bottom rulers change Y; left/right rulers change X. Hold Shift for fine adjustment or Ctrl for very fine adjustment.",
        ["calibration.adjustX"] = "Horizontal position (X) fine adjustment",
        ["calibration.adjustY"] = "Vertical position (Y) fine adjustment",
        ["calibration.precisionHint"] = "Main guides are thick and helper guides are thin. The primary display's 25%, 50%, and 75% physical references are drawn across every display.",
        ["calibration.noTarget"] = "No adjustable target display was found.",

        ["transition.title"] = "Transition behaviour",
        ["transition.align"] = "Physical alignment",
        ["transition.alignHint"] = "The cursor keeps its real height on the desk when crossing screens, instead of its pixel row.",
        ["transition.rawInput"] = "Edge assisted crossing (raw input)",
        ["transition.rawInputHint"] = "Reconstructs the rest of a movement from raw HID data when Windows pins the cursor to the edge of the desktop. This is what makes crossings into a physically adjacent but pixel offset display reliable.",
        ["transition.preventLoss"] = "Never lose the cursor",
        ["transition.preventLossHint"] = "Recovers a cursor that ended up in the dead space of an L shaped arrangement.",
        ["transition.speed"] = "Match pointer speed per display",
        ["transition.speedHint"] = "The same hand movement covers the same physical distance on every screen. Changes the Windows pointer speed setting and restores it on exit.",
        ["transition.resistance"] = "Border resistance",
        ["transition.resistanceHint"] = "How far the pointer must be pushed into a border before it crosses. 0 disables it.",
        ["transition.wrap"] = "Wrap around the desktop",
        ["transition.wrap.none"] = "Off",
        ["transition.wrap.horizontal"] = "Horizontal",
        ["transition.wrap.vertical"] = "Vertical",
        ["transition.wrap.both"] = "Horizontal and vertical",

        ["drag.title"] = "Window dragging",
        ["drag.mode"] = "Scaling mode",
        ["drag.mode.off"] = "Off",
        ["drag.mode.drop"] = "Measured — correct on drop",
        ["drag.mode.live"] = "Aggressive — live while dragging",
        ["drag.modeHint"] = "In aggressive mode the window is rescaled the moment it crosses a DPI boundary so it keeps its real size, and the point you grabbed stays under the cursor.",
        ["drag.grab"] = "Preserve the grab point",
        ["drag.seamless"] = "Cross-display window continuity",
        ["drag.seamlessHint"] = "Matches the transition only while a window spans two or more displays. As soon as it belongs to one display, all corrections stop and Windows resumes normal sizing.",
        ["drag.seamlessWarningTitle"] = "Experimental feature",
        ["drag.seamlessWarning"] = "This may not work as expected, or may not work at all, with some applications, special window types, or display/DPI layouts. Leave it disabled if it causes problems.",
        ["drag.skipUnaware"] = "Skip DPI unaware windows",
        ["drag.skipUnawareHint"] = "Windows bitmap stretches those; resizing them from the outside produces a blurry result.",
        ["drag.skipMaximised"] = "Skip maximised windows",
        ["drag.throttle"] = "Live update interval",

        ["rules.title"] = "Application rules",
        ["rules.hint"] = "Change the behaviour while specific applications are in the foreground. Enter the process name without its extension; a trailing * matches a prefix.",
        ["rules.process"] = "Process name",
        ["rules.action"] = "Behaviour",
        ["rules.action.correct"] = "Normal correction",
        ["rules.action.passthrough"] = "Do not interfere",
        ["rules.action.noscale"] = "Correct the cursor, no window scaling",
        ["rules.games"] = "Games and full screen",
        ["rules.exclusive"] = "Pause in exclusive full screen",
        ["rules.exclusiveHint"] = "In Direct3D exclusive full screen the cursor is already confined to one display, so staying out of the way is the safest option.",
        ["rules.borderless"] = "Keep correcting in borderless full screen",
        ["rules.anticheat"] = "Stand down while anti-cheat is running",
        ["rules.anticheatHint"] = "Kernel level anti-cheat can read injected cursor movement as automation. The safe default is to get out of the way.",
        ["rules.deepIntegration"] = "Advanced Windows integration",
        ["rules.deepIntegrationHint"] = "Uses administrator elevation, stricter hook monitoring and automatic recovery for elevated applications, virtual desktops, and desktop/session switches. Direct access to the Winlogon secure desktop is restricted by Windows.",

        ["startup.title"] = "Startup and reliability",
        ["startup.enable"] = "Start with Windows",
        ["startup.elevated"] = "Start as administrator (Task Scheduler)",
        ["startup.elevatedHint"] = "A hook installed by a standard user process is ignored while an elevated window has focus. A logon scheduled task grants those rights without a UAC prompt at every sign in.",
        ["startup.minimised"] = "Start minimised",
        ["startup.tray"] = "Show the tray icon",
        ["startup.hookTimeout"] = "Raise the hook timeout",
        ["startup.hookTimeoutHint"] = "Windows silently removes low level hooks whose callback runs late. Raising this removes that class of failure; it takes effect after signing out and back in.",
        ["startup.method"] = "Registration method",
        ["startup.method.task"] = "Scheduled task (elevated)",
        ["startup.method.run"] = "Registry Run key",
        ["startup.method.none"] = "Not registered",
        ["startup.language"] = "Interface language",
        ["startup.languageHint"] = "Takes effect the next time the application starts.",
        ["startup.repair"] = "Repair the registration",
        ["settings.title"] = "Back up and transfer settings",
        ["settings.hint"] = "The JSON file contains all general settings, display profiles, calibrations, resistance options, and application rules.",
        ["settings.export"] = "Export settings",
        ["settings.import"] = "Import settings",
        ["settings.fileFilter"] = "CaYaScreenBridge settings (*.json)|*.json|All files (*.*)|*.*",
        ["settings.exportSuccess"] = "Settings were exported successfully.",
        ["settings.exportFailed"] = "Settings could not be exported.",
        ["settings.importSuccess"] = "Settings were validated, saved, and applied live.",
        ["settings.importFailed"] = "The selected file could not be imported; current settings were left unchanged.",

        ["diag.title"] = "Diagnostics",
        ["diag.copy"] = "Copy the log",
        ["diag.openFolder"] = "Open the folder",
        ["diag.verbose"] = "Verbose logging",
        ["diag.rebuild"] = "Rebuild the layout",
        ["diag.rearm"] = "Re-arm the hook",
        ["diag.live"] = "Live cursor",

        ["tray.show"] = "Open settings",
        ["tray.pause"] = "Pause correction",
        ["tray.resume"] = "Resume correction",
        ["tray.rebuild"] = "Rebuild the layout",
        ["tray.exit"] = "Exit",
        ["tray.tipRunning"] = "CaYaScreenBridge — active",
        ["tray.tipPaused"] = "CaYaScreenBridge — paused",
    };
}

/// <summary>Resolves a localised string in XAML: <c>Text="{ui:S nav.overview}"</c>.</summary>
public sealed class SExtension : MarkupExtension
{
    public SExtension()
    {
    }

    public SExtension(string key) => Key = key;

    public string Key { get; set; } = string.Empty;

    public override object ProvideValue(IServiceProvider serviceProvider) => Loc.Get(Key);
}

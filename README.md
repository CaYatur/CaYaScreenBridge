<div align="center">

<img src="src/CaYaScreenBridge.Windows/Assets/app-256.png" width="120" alt="CaYaScreenBridge" />

# CaYaScreenBridge

**Farklı DPI'lı ekranlar arasında imleci fiziksel olarak hizalı tutan Windows uygulaması.**

*A Windows utility that keeps the mouse cursor physically aligned across mixed DPI displays.*

</div>

---

## Sorun

Windows her ekranı yalnızca bir piksel dikdörtgeni olarak görür. 27" 4K bir panel ile 24" Full HD bir
panel neredeyse aynı piksel yüksekliğine sahiptir ama fiziksel olarak tamamen farklıdır. İmleç
kenardan geçerken piksel satırını koruduğu için masanızda bambaşka bir yükseklikte belirir — 4K
ekranın ortasından çıkan imleç, yan ekranın en altına düşer.

CaYaScreenBridge, Windows'un piksel uzayına paralel olarak **milimetre cinsinden ikinci bir konum**
tutar, her hareketi bu fiziksel uzayda çözer ve sonucu ancak en sonda piksele çevirir.

## Neler yapar

| | |
|---|---|
| **Fiziksel hizalama** | İmleç ekran değiştirirken masadaki gerçek yüksekliğini korur. |
| **Yörünge farkındalığı** | Hedef ekran, hareketin bittiği nokta ile değil, hareketin geçtiği yol ile belirlenir. Hızlı bir çapraz savurma, gerçekten üzerinden geçtiği ekrana iner. |
| **Kenar destekli geçiş** | Windows imleci masaüstü kenarına sabitlediğinde hareket ham HID verisinden yeniden kurulur. Fiziksel olarak komşu ama piksel olarak kaymış ekranlara geçişi güvenilir yapan şey budur. |
| **İmleç kaybolmaz** | L biçimli yerleşimlerde boşluğa düşen imleç, gidiş yönündeki en yakın ekrana yansıtılır. |
| **Pencere sürükleme** | Pencere DPI sınırını geçtiği anda gerçek boyutunu koruyacak şekilde yeniden ölçeklenir; tuttuğunuz nokta imlecin altında kalır. |
| **Oyun ve tam ekran** | Özel tam ekran, kenarlıksız tam ekran ve anti-cheat durumları ayrı ayrı algılanır ve gerektiğinde uygulama tamamen yoldan çekilir. |
| **Kendini onarır** | Kanca düşerse yeniden kurulur; uyku, oturum kilidi, ekran değişimi ve bozuk yapılandırma dosyası için ayrı kurtarma yolları vardır. |
| **Görsel yerleşim editörü** | Ekranları gerçek göreli boyutlarıyla, milimetre uzayında sürükleyerek düzenlersiniz. |

## Kurulum

**Kurulum dosyası** — [Releases](https://github.com/CaYatur/CaYaScreenBridge/releases) sayfasından
`CaYaScreenBridge-x.y.z-setup.exe` dosyasını indirin.

**Taşınabilir** — aynı sayfadaki `-portable-win-x64.zip` arşivini açıp `CaYaScreenBridge.exe`
dosyasını çalıştırın. .NET kurulumu gerekmez, paket kendi çalışma zamanını taşır.

Gereksinim: Windows 10 sürüm 1809 (10.0.17763) veya üzeri, x64.

### Başlangıçta açılma

Uygulama kendi oturum açma görevini **Görev Zamanlayıcı** üzerinden kaydeder. Bu bilinçli bir
tercih: standart kullanıcı olarak kurulan bir düşük seviye fare kancası, yükseltilmiş bir pencere
öndeyken yok sayılır. Yani yönetici olarak açılmış bir oyunun veya Görev Yöneticisi'nin üzerindeyken
düzeltme sessizce çalışmayı bırakır. Oturum açma görevi bu yetkiyi her açılışta UAC istemi
göstermeden verir.

Üç kademeli yedek yol vardır — Görev Zamanlayıcı COM arayüzü, `schtasks` komut satırı, ve son çare
olarak `HKCU\...\Run` anahtarı. Uygulama her açılışta kaydını doğrular ve yol değiştiyse (güncelleme,
klasör taşıma) yeniden yazar.

## Nasıl çalışır

### Koordinat modeli

Her ekran iki dikdörtgenle temsil edilir: Windows'un kullandığı piksel dikdörtgeni, ve panelin
masadaki gerçek yerini tarif eden milimetre dikdörtgeni. Fiziksel boyut **EDID**'den okunur (panelin
kendi bildirdiği gerçek ölçüler), yoksa DPI'dan tahmin edilir, her durumda kullanıcı düzeltebilir.

```
milimetre = fizikselKonum + (piksel - pikselKonum) / pikselPerMm
```

Yerleşim, Windows ekran ayarlarındaki komşuluk ilişkileri korunarak milimetre uzayında yeniden kurulur:
piksel uzayında birbirine değen paneller milimetre uzayında da değer, ortak kenar boyunca kayma
oranı korunur.

### Geçiş çözücü

Her fare hareketi için:

1. Hareket kaynak ekranın içindeyse hiçbir şey yapılmaz — bu, olayların büyük çoğunluğudur ve bir
   dikdörtgen testine mal olur.
2. Ekrandan çıkıyorsa hareket milimetre uzayına çevrilir ve hedef nokta hesaplanır.
3. Hedef nokta bir ekranın üzerindeyse iş biter.
4. Değilse hareket **doğru parçası olarak** tüm ekranlarla kesiştirilir; yol üzerindeki ilk ekran
   seçilir ve konum o ekrana kırpılır. Dar bir ekranı aşan hızlı hareketler böyle doğru iner.
5. O da yoksa sarma (etkinse) denenir.
6. Hâlâ yoksa, gidiş yönündeki en yakın ekrana yansıtılır. **İmleç asla boşlukta kalmaz.**

Otoriter konum milimetre olanıdır ve **asla yuvarlanmış pikselden yeniden türetilmez**. Bu yüzden bir
sınırı bin kez ileri geri geçmek imleci başladığı yere döndürür; yuvarlama hatası birikmez.
Depoda bunu 1000 geçiş üzerinden doğrulayan bir test var.

### Kenar destekli geçiş

Windows, imleci masaüstünün dış kenarına dayadığında düşük seviye kanca ateşlenmeye devam eder ama
bildirilen konum değişmez — yani niyet görünmez olur. Ham girdi (`WM_INPUT`) o sırada gerçek cihaz
deltasını vermeye devam eder. Ancak bu delta cihaz birimindedir ve arada işaretçi ivmesi vardır.

Uygulama ivme eğrisini yeniden yazmaya çalışmaz: imleç serbestçe hareket ederken ham delta ile
gerçek piksel hareketi arasındaki oranı **üç hız kovasında** öğrenir (ivme hıza bağlı olduğu için tek
bir ortalama yetmez), ve öğrenilen oranı yalnızca o an tıkalı olan eksen için uygular.

### Pencere sürükleme

Sürükleme başladığında pencerenin fiziksel boyutu ve tutma noktasının orana göre yeri kaydedilir.
Pencere DPI sınırını geçtiğinde hedef dikdörtgen bu iki değerden çözülür.

İki ayrıntı bunu pratikte çalışır kılar:

- Her sınır geçişinden kısa bir süre sonra dikdörtgen **yeniden uygulanır**. DPI farkında bir
  uygulama bizim `SetWindowPos` çağrımızdan sonra `WM_DPICHANGED` alır ve kendi seçtiği boyuta geçer;
  gecikmeli ikinci geçiş bizim ölçümüzü geri koyar.
- Her `SetWindowPos` girdi iş parçacığının dışında yapılır. Kanca geri çağrısı içinden başka bir
  sürecin penceresine yazmak, o süreç kadar bloke olmak demektir — ve Windows'un kancayı düşürmesinin
  en klasik yolu tam olarak budur.

DPI farkında olmayan pencereler varsayılan olarak atlanır: Windows onları bitmap olarak esnetir,
dışarıdan yeniden boyutlandırmak bulanık ve kaymış sonuç verir.

> **Not:** Masaüstünden dosya sürükleyip bırakırken imleç hizalaması tam olarak çalışır, ancak
> sürükleme sırasında görünen yarı saydam dosya görüntüsü OLE tarafından kaynak DPI'da çizilir ve
> süreç dışından yeniden ölçeklenemez. Bırakma işleminin kendisi doğru konuma iner.

### Oyunlar ve tam ekran

| Durum | Varsayılan |
|---|---|
| Direct3D özel tam ekran | Duraklat — imleç zaten tek ekrana kilitli, müdahale etmenin faydası yok riski var |
| Kenarlıksız tam ekran | İmleci düzelt, pencereyi asla yeniden boyutlandırma |
| Anti-cheat çalışıyor | Tamamen dur — enjekte edilmiş imleç hareketi otomasyon olarak okunabilir |
| Uygulama kuralı | Süreç adına göre üç davranıştan biri |

Anti-cheat kontrolü, açıkça yazılmış bir kuralın bile üzerindedir. Yanlış tarafta olmanın bedelini
uygulama değil kullanıcının hesabı öder.

### Güvenilirlik

Bu tür bir aracın gerçek sınavı üç hafta ve kırk uyku döngüsü sonra hâlâ çalışıp çalışmadığıdır.

- **Kanca kendi iş parçacığında.** `WH_MOUSE_LL` geri çağrısı kancayı kuran iş parçacığında
  koşturulur ve Windows, `LowLevelHooksTimeout` süresini aşan kancaları haber vermeden düşürür.
  Kanca arayüzde yaşasaydı yavaş bir XAML düzeni veya kalıcı bir iletişim kutusu uygulamayı sessizce
  devre dışı bırakırdı.
- **Gözcü.** Düşük seviye bir kancanın hâlâ zincirde olup olmadığını soran bir API yok. Tek güvenilir
  sinyal bir çelişkidir: imleç gözle görülür şekilde hareket etmiş, ama geri çağrıya hiçbir olay
  ulaşmamış. Bu ancak kanca gitmişse olur, ve bu tespit edildiğinde kanca yeniden kurulur.
- **Sistem olayları.** Uyku dönüşü, oturum kilidi/açılması, kullanıcı değişimi ve ekran topolojisi
  değişimi için ayrı kurtarma yolları vardır. Ekran değişimi geciktirilerek işlenir; Windows bu olayı
  yerleşim oturana kadar birkaç kez ateşler.
- **Yapılandırma.** Yazma işlemi geçici dosya üzerinden atomik olarak yapılır ve önceki iyi kopya
  yedek olarak tutulur; bozuk dosya yedekten kurtarılır, sessizce sıfırlanmaz.
- **Kanca zaman aşımı.** `LowLevelHooksTimeout` isteğe bağlı olarak yükseltilir. Geri çağrı normal
  koşullarda bu bütçenin çok altındadır, ama ağır yük altında takılan bir makine sınırı aşabilir ve
  kullanıcı bunu "uygulama rastgele durdu" olarak yaşar.

## Yapılandırma

Ayarlar `%LOCALAPPDATA%\CaYaScreenBridge\config.json` altındadır. Günlükler aynı klasördeki `logs`
dizinindedir ve yedi gün sonra silinir.

Ekran kalibrasyonu **profil** olarak saklanır ve profil kimliği bağlı ekranların kümesinden türetilir.
Aynı dizüstü bilgisayarı evde ve ofiste farklı yerleşimlere takmak, her ikisinin de kendi
kalibrasyonunu korumasını sağlar.

## Kaynaktan derleme

```bash
git clone https://github.com/CaYatur/CaYaScreenBridge.git
cd CaYaScreenBridge

dotnet build CaYaScreenBridge.sln -c Release
dotnet test tests/CaYaScreenBridge.Core.Tests
```

WPF yalnızca Windows üzerinde derlenir. Çekirdek kütüphane (`CaYaScreenBridge.Core`) platformdan
bağımsız `net8.0` hedefler ve algoritmanın tamamı orada yaşadığı için testler her yerde koşar.

İkonu yeniden üretmek için:

```bash
pip install Pillow
python3 build/icon/generate_icon.py
```

## Proje yapısı

```
src/CaYaScreenBridge.Core/          Platformdan bağımsız çekirdek
  Geometry/                         Vektör, dikdörtgen, doğru-dikdörtgen kesişimi
  Model/                            Ekran modeli, milimetre yerleşimi, EDID ayrıştırma
  Algorithm/                        Geçiş çözücü, ham delta kalibrasyonu, pencere çözücü, politika
  Config/                           Yapılandırma modeli ve dayanıklı depolama
src/CaYaScreenBridge.Windows/       Windows katmanı ve arayüz
  Native/                           P/Invoke tanımları
  Platform/                         Monitör sayımı, EDID kayıt defteri okuması
  Engine/                           Kanca iş parçacığı, ön plan gözlemcisi, sürükleme, orkestrasyon
  System/                           Başlangıç kaydı, tek örnek, dosya günlüğü
  Ui/                               WPF arayüz, tepsi simgesi, yerleşim editörü
tests/CaYaScreenBridge.Core.Tests/  Algoritma testleri
```

## Lisans

MIT — bkz. [LICENSE](LICENSE).

<div align="center">
<sub><a href="https://cayadev.com">cayadev.com</a></sub>
</div>

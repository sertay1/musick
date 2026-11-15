# Musick — by SertAy

**Sürüm 1.0.0 · Windows x64**

Kick yayınlarında sohbetten müzik isteği toplamak, çalma sırasını yönetmek ve OBS'ye şarkı bilgisi aktarmak için masaüstü uygulaması.

- Spotify masaüstü uygulamasıyla doğrudan oynatma ve durum eşitleme.
- YouTube / YouTube Music bağlantıları, müzik kategorisi kontrolü ve gömülü YouTube oynatıcı.
- Platformları ayrı ayrı etkinleştirme veya kapatma.
- Spotify ve YouTube listelerini kütüphaneye aktarma, istek kuyruğu ve otomatik şarkı havuzu.
- Kick komutları, rol izinleri, bekleme süreleri ve etkinlik günlüğü; komut ayarlarını JSON ile içe/dışa aktarma.
- API bilgileri ve hesap oturumları hariç tercihleri JSON ile yedekleme / taşıma.
- Özelleştirilebilir OBS katmanları; Türkçe / İngilizce ve açık / koyu arayüz.
- GitHub Release üzerinden kullanıcı tarafından başlatılan güncelleme kontrolü.

## Kullanıcı kurulumu

GitHub **Releases** bölümünden Musick-Setup.exe dosyasını indirin. Bu kurulum dosyası tek başına dağıtılabilir; .NET çalışma zamanı pakete dahildir. WebView2 eksikse kurulum sırasında internetten yüklenir.

Eski Musick'i sistem tepsisinden tamamen kapatıp kurulumu çalıştırın. Spotify ve Kick hesaplarını Ayarlar'dan bağlayın. Halka açık YouTube oynatma hesap bağlantısı gerektirmez; hesap listeleri ve beğeni eşitlemesi için YouTube ile Bağlan düğmesini kullanın. Google istemcisi yayıncı tarafından bir kez hazırlanır; kullanıcıların Cloud projesi açması gerekmez. Hesaplar, oturumlar ve kişisel ayarlar kaynak koduna dahil değildir; başka bir bilgisayarda yeniden bağlantı kurulur.

YouTube tarayıcı bağlantısının Google Cloud gereksinimleri ve oynatma yedeği: [YouTube bağlantısı](docs/YOUTUBE-TARAYICI.md).

## Kaynak kodundan geliştirme

Gereksinimler: Windows, .NET 9 SDK, Node.js 20 veya üzeri ve WebView2 Runtime.

```powershell
cd src/MusicBot.Web/clientapp
npm ci
npm run build
cd ../../..
dotnet restore MusicBot.sln
dotnet run --project src/MusicBot.Desktop
```

Arayüz kaynakları src/MusicBot.Web/clientapp/src/ dizinindedir. wwwroot/ derleme çıktısıdır; doğrudan düzenlenmez. Canlı yenileme için backend açıkken clientapp/ içinde npm start çalıştırılabilir.

## Test ve kurulum paketi

```powershell
dotnet test src/MusicBot.Tests --filter "FullyQualifiedName!~LiveTests"
powershell -ExecutionPolicy Bypass -File scripts/Package-Release.ps1 -Version 1.0.0
```

Paketleme betiği arayüzü derler, çevrimdışı testleri çalıştırır ve kendi .NET çalışma zamanını içeren Windows kurulumunu hazırlar. Canlı Kick bağlantısı testi standart kontrollerin dışındadır. Çıktılar artifacts/<sürüm>/<runtime>/ altına yazılır. Alternatif NUKE komutu:

```powershell
build.cmd --target Pack --configuration Release --runtime win-x64 --version 1.0.0
```

## Proje yapısı

- MusicBot.Core: şarkı, kuyruk modelleri ve arayüzler.
- MusicBot.Web: yerel API, Spotify/Kick servisleri, SQLite ve React arayüzü.
- MusicBot.Desktop: WPF, WebView2, sistem tepsisi ve masaüstü başlangıcı.
- MusicBot.Tests: otomatik kontroller.
- design/console-concept/: mevcut uygulamaya uygulanmamış alternatif arayüz örneği. HTML dosyası tarayıcıda bağımsız açılır ve yalnızca örnek veriler kullanır.

Masaüstü API adresi http://127.0.0.1:3050, kişisel veri dizini %AppData%/MusicBot'tur. Spotify şarkıları Spotify'dan, YouTube şarkıları gömülü oynatıcıdan çalınır; yerel dosya oynatıcısı da kodda mevcuttur.

## Hesap yapılandırması

Spotify ve Kick geliştirici uygulaması bilgileri Ayarlar'dan girilebilir. Geliştirme sırasında özel bilgiler için .NET user-secrets kullanılabilir:

```powershell
dotnet user-secrets set "Spotify:ClientId" "UYGULAMA_KIMLIGI" --project src/MusicBot.Web
dotnet user-secrets set "Spotify:ClientSecret" "UYGULAMA_SIRRI" --project src/MusicBot.Web
```

Kişisel appsettings.user.json, çerezler, token'lar, veritabanı ve özel anahtarlar GitHub'a yüklenmez. Depodaki appsettings.json dağıtılabilir varsayılan yapılandırmadır.

## Lisans ve atıf

SertAy'ın lisanslama yetkisine sahip olduğu özgün Musick katkıları [SertAy lisansına](LICENSE-SERTAY.txt) tabidir. Lisans, atıf ve değişikliklerin belirtilmesini gerektirir ve ticari kullanımı sınırlar.

Musick, [overabstractor/MusicBot](https://github.com/overabstractor/MusicBot) projesini temel alır. Devralınan kodun hakları ilgili hak sahiplerine aittir. Üçüncü taraf bileşenlerin lisansları [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) dosyasında belirtilir.

GitHub hazırlığı için [yayımlama rehberine](docs/GITHUB-YAYINLAMA.md) bakın.

Kullanılmayan bağımlılıklar kaldırıldı; npm denetiminde bilinen güvenlik bulgusu kalmadı; [yerel denetim notuna](docs/BAGIMLILIK-KONTROLU.md) bakın.

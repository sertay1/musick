<<<<<<< HEAD
<h1 align="center">Musick</h1>

<div align="center">
  <img src="src/MusicBot.Web/clientapp/public/musick-logo-horizontal.png" alt="Musick logo" width="240">
  <p><strong>by SertAy · v1.0.0 · Windows x64</strong></p>
  <p><strong>(EN)</strong> Take music requests from Kick chat, manage Spotify and YouTube playback, and show the current song in OBS.</p>
  <p><strong>(TR)</strong> Kick sohbetinden şarkı istekleri al, Spotify ve YouTube çalma sırasını yönet, çalan şarkıyı OBS'de göster.</p>
  <a href="#en-setup-and-usage-guide"><img src="https://img.shields.io/badge/Language-English-blue?style=for-the-badge" alt="English"></a>
  <a href="#tr-kurulum-ve-kullanım-rehberi"><img src="https://img.shields.io/badge/Dil-T%C3%BCrk%C3%A7e-red?style=for-the-badge" alt="Türkçe"></a>
  <a href="https://github.com/sertay1/musick/releases"><img src="https://img.shields.io/badge/Download-Windows-618635?style=for-the-badge" alt="Download for Windows"></a>
</div>

---

## (EN) Setup and Usage Guide

### ✨ Features

- **Live requests:** Take requests from Kick chat and follow commands and queue changes in the activity log.
- **Spotify and YouTube:** Control Spotify desktop playback and play YouTube music in Musick.
- **Library and automatic queue:** Import playlists and keep music playing from your selected pool when requests run out.
- **Chat controls:** Customize command aliases, role permissions, cooldowns, and bot replies.
- **OBS overlays:** Show the current track and upcoming songs using customizable layouts.

<div align="center">
  <img src="docs/screenshots/yayin-masasi.png" alt="Musick broadcast desk with playback, song queue, and request feed" width="1000">
  <p><em>Broadcast desk preview with sample requests</em></p>
</div>

### ⚙️ Step 1 — Before you start

Musick runs on Windows x64. Install and open Spotify desktop if you plan to play Spotify tracks. Have your Kick channel name and the account you want the bot to use ready.

### 💾 Step 2 — Install Musick

1. Download the Windows setup EXE from [Releases](https://github.com/sertay1/musick/releases).
2. If you're updating, quit Musick from the system tray first.
3. Run the installer. It includes the .NET runtime and downloads WebView2 if needed.

### 🔑 Step 3 — Get developer credentials

- **Kick:** Create an app in [Kick Developer Settings](https://kick.com/settings/developer). Musick requests `user:read` for account details and `chat:write` for bot replies.
- **Spotify:** If the app asks for developer credentials, create an app in the [Spotify Developer Dashboard](https://developer.spotify.com/dashboard). Use the redirect address shown in Musick's Spotify settings.
- **YouTube:** Public music playback works without signing in. Playlist and like sync uses Google's sign-in flow. See the [YouTube setup guide](docs/YOUTUBE-TARAYICI.md) for account and distribution requirements.

### 📝 Step 4 — Connect your accounts

In **Settings → Accounts**, enter your credentials under the corresponding platform. Set the Kick channel to the chat that receives commands. Sign in with the account that should send replies. Connect Spotify there if you use it.

### 🎵 Step 5 — Take requests and add an OBS overlay

1. Send `!ekle song name` in Kick chat. The request appears in the broadcast desk. Use `!atla` to skip if you have permission.
2. Use **Commands & Permissions** to set aliases, permissions, cooldowns, and reply templates.
3. Pick a design in **OBS Overlays**. Copy the URL from the OBS setup area, paste it into a **Browser** source in OBS, and use the dimensions shown in Musick.

<div align="center">
  <img src="docs/screenshots/obs-kaset.png" alt="Cassette-style OBS music overlay" width="312">
  <p><em>Cassette overlay preview</em></p>
</div>

### 🔗 Contact

**SertAy** · [Linktree](https://linktr.ee/yamanx) · [Report an issue](https://github.com/sertay1/musick/issues)

---

## (TR) Kurulum ve Kullanım Rehberi

### ✨ Özellikler

- **Canlı istekler:** Kick sohbetinden şarkı isteği al. Komutları ve sıra değişikliklerini etkinlik günlüğünde takip et.
- **Spotify ve YouTube:** Spotify masaüstü oynatıcısını kontrol et ve YouTube müziklerini Musick içinde çal.
- **Kütüphane ve otomatik kuyruk:** Listeleri içe aktar. İstekler bittiğinde seçtiğin havuzdan müzik çalmaya devam et.
- **Sohbet komutları:** Takma adları, rol izinlerini, bekleme sürelerini ve bot yanıtlarını düzenle.
- **OBS katmanları:** Çalan şarkıyı ve sıradaki parçaları özelleştirilebilir tasarımlarla yayına ekle.

<div align="center">
  <img src="docs/screenshots/yayin-masasi.png" alt="Musick yayın masası, oynatıcı, çalma sırası ve istek akışı" width="1000">
  <p><em>Örnek isteklerle yayın masası önizlemesi</em></p>
</div>

### ⚙️ 1. Aşama — Başlamadan önce

Musick Windows x64 üzerinde çalışır. Spotify'dan şarkı çalacaksan masaüstü Spotify uygulamasını kur ve aç. Kick kanal adını ve botun sohbete yanıt vermesini istediğin hesabı hazırla.

### 💾 2. Aşama — Musick'i kur

1. [Releases](https://github.com/sertay1/musick/releases) bölümünden Windows kurulum EXE'sini indir.
2. Güncelleme yapıyorsan önce Musick'i sistem tepsisinden tamamen kapat.
3. Kurulumu çalıştır. .NET çalışma zamanı pakete dahildir. Gerektiğinde WebView2 indirilir.

### 🔑 3. Aşama — Geliştirici bilgilerini al

- **Kick:** [Kick geliştirici ayarları](https://kick.com/settings/developer) sayfasından bir uygulama oluştur. Musick, bağlı hesabın bilgilerini okumak için `user:read`, sohbet mesajı göndermek için `chat:write` iznini ister.
- **Spotify:** Uygulama geliştirici bilgisi isterse [Spotify Developer Dashboard](https://developer.spotify.com/dashboard) üzerinden bir uygulama oluştur. Musick'in Spotify ayarlarında gösterilen dönüş adresini kullan.
- **YouTube:** Halka açık şarkıları çalmak için giriş gerekmez. Kendi listelerini ve beğenilerini eşitlemek, Google hesabıyla giriş gerektirir. Hesap ve dağıtım koşulları için [YouTube bağlantı rehberine](docs/YOUTUBE-TARAYICI.md) bak.

### 📝 4. Aşama — Hesapları bağla

**Ayarlar → Hesaplar** bölümünde geliştirici bilgilerini ilgili platforma gir. Kick kanal adına komutların çalışacağı sohbetin kanalını yaz. Botun cevap vermesini istediğin hesapla giriş yap. Spotify kullanacaksan hesabını aynı menüden bağla.

### 🎵 5. Aşama — Şarkı isteği al ve OBS'ye ekle

1. Kick sohbetine `!ekle şarkı adı` yaz. İsteği **Yayın masası** ekranından takip et. Yetkin varsa `!atla` ile sonraki şarkıya geç.
2. **Komutlar & Yetkiler** ekranında takma adları, izinleri, bekleme sürelerini ve yanıt şablonlarını düzenle.
3. **OBS Overlay'leri** bölümünde bir tasarım seç. **OBS'ye Ekle** alanındaki adresi OBS'de bir **Tarayıcı** kaynağına yapıştır ve gösterilen ölçüleri gir.

<div align="center">
  <img src="docs/screenshots/obs-kaset.png" alt="Kaset biçiminde OBS müzik katmanı" width="312">
  <p><em>Kaset katmanı önizlemesi</em></p>
</div>

### 🔗 İletişim

**SertAy** · [Linktree](https://linktr.ee/yamanx) · [Sorun bildir](https://github.com/sertay1/musick/issues)

---

## 🛠️ Development / Geliştirme

Windows · .NET 9 SDK · Node.js · WebView2 Runtime
=======
<p align="center">
  <img src="src/MusicBot.Web/clientapp/public/musick-logo-horizontal.png" alt="Musick" width="260">
</p>

# Musick

**by SertAy · v1.0.0 · Windows x64**

**TR** · Musick, Kick yayınlarında sohbetten Spotify ve YouTube şarkı istekleri almanı, çalma sırasını yönetmeni ve çalan şarkıyı OBS'de göstermeni sağlayan bir masaüstü uygulaması.

**EN** · Musick is a desktop app for Kick streamers. Take Spotify and YouTube song requests from chat, manage the playback queue, and show what's playing in OBS.

[İndir / Download](https://github.com/sertay1/musick/releases) · [Türkçe rehber](#türkçe-kurulum-ve-kullanım) · [English guide](#english-setup-and-usage) · [Sorun bildir / Report an issue](https://github.com/sertay1/musick/issues)

## Türkçe kurulum ve kullanım

### Özellikler

- Kick sohbetinden `!ekle` ile şarkı isteği, `!atla` ile sıradaki şarkıya geçme. Komut takma adları, rol izinleri, bekleme süreleri ve bot yanıtları uygulamadan düzenlenebilir.
- Spotify masaüstü oynatıcısıyla eşitleme ve YouTube müzik istekleri için gömülü oynatıcı.
- Yayın masasında çalma sırası, canlı istek akışı ve etkinlik günlüğü. Kütüphaneden liste aktarımı ve sıra boşaldığında otomatik kuyruk.
- OBS için özelleştirilebilir şarkı katmanları. Türkçe ve İngilizce arayüz, açık ve koyu tema.

### 1. Musick'i kur

[Releases](https://github.com/sertay1/musick/releases) sayfasından Windows kurulum EXE'sini indirip çalıştır. .NET çalışma zamanı pakete dahildir. WebView2 eksikse kurulum sırasında indirilir. Güncelleme yapıyorsan önce Musick'i sistem tepsisinden tamamen kapat.

### 2. Hesapları bağla

- **Kick** · **Ayarlar → Hesaplar** bölümüne, komutların çalışacağı kanalın adını yaz. Sohbete bot hesabının cevap vermesini istiyorsan bağlantıyı o hesapla kur. Geliştirici uygulaması bilgileri için [Kick geliştirici ayarlarını](https://kick.com/settings/developer) aç.
- **Spotify** · Hesabını Ayarlar'dan bağla ve Spotify masaüstü uygulamasını aç. Oynatma ve ses kontrolü Spotify üzerinden yürür.
- **YouTube** · Halka açık şarkıları çalmak için hesap bağlantısı gerekmez. Kendi listelerini ve beğenilerini eşitlemek için [YouTube bağlantı rehberini](docs/YOUTUBE-TARAYICI.md) izle.

### 3. Şarkı isteği al ve OBS'ye ekle

1. Kick sohbetine `!ekle şarkı adı` yaz ve isteğin **Yayın masası** ekranında göründüğünü kontrol et.
2. **Komutlar & Yetkiler** bölümünden kimlerin şarkı isteyebileceğini, şarkı atlayabileceğini ve botun ne yazacağını ayarla.
3. **OBS Overlay'leri** bölümünde bir tasarım seç. **OBS'ye Ekle** alanındaki bağlantıyı kopyala ve OBS'de bir **Tarayıcı** kaynağına yapıştır.

## English setup and usage

### Features

- Kick chat requests and skip commands with configurable aliases, role permissions, cooldowns, and bot replies.
- Spotify desktop playback controls and an embedded player for YouTube music requests.
- Live request feed, activity log, playlist imports, and an automatic queue that fills in when requests run out.
- Customizable OBS music overlays, Turkish and English interface, and light and dark themes.

### 1. Install Musick

Download the Windows installer from [Releases](https://github.com/sertay1/musick/releases) and run it. The .NET runtime is bundled. The installer downloads WebView2 if needed. If upgrading, quit Musick from the system tray first.

### 2. Connect your accounts

- **Kick** · In **Settings → Accounts**, enter the channel where commands should run. Connect the account you want to send chat replies. Developer credentials are available in [Kick developer settings](https://kick.com/settings/developer).
- **Spotify** · Connect your account in Settings and open the Spotify desktop app.
- **YouTube** · Public music playback works without signing in. To sync your own playlists and likes, follow the [YouTube setup guide in Turkish](docs/YOUTUBE-TARAYICI.md).

### 3. Take requests and add an OBS overlay

1. Send `!ekle song name` in your Kick chat and check the request on the broadcast dashboard.
2. Open the commands and permissions screen to configure who can request or skip songs and what the bot replies.
3. Pick a design in **OBS Overlays**, copy its URL, and paste it into an OBS **Browser** source.

## Geliştirme / Development

Windows, .NET 9 SDK, Node.js ve WebView2 Runtime gerekir. Musick; WPF, ASP.NET Core, React ve SQLite kullanır.
>>>>>>> 627ac7b69e8348a81db28b94ad57780b9ba18370

```powershell
cd src/MusicBot.Web/clientapp
npm ci
npm run build
cd ../../..
dotnet run --project src/MusicBot.Desktop
```

<<<<<<< HEAD
=======
Test ve Windows kurulum paketi / Tests and Windows installer:

>>>>>>> 627ac7b69e8348a81db28b94ad57780b9ba18370
```powershell
dotnet test src/MusicBot.Tests --filter "FullyQualifiedName!~LiveTests"
powershell -ExecutionPolicy Bypass -File scripts/Package-Release.ps1 -Version 1.0.0
```

<<<<<<< HEAD
## 📄 License / Lisans

[LICENSE-SERTAY.txt](LICENSE-SERTAY.txt) covers contributions that SertAy is authorized to license. Rights to inherited code remain with their respective holders. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for third-party licenses.

SertAy'ın lisanslama yetkisine sahip olduğu Musick katkılarının koşulları [LICENSE-SERTAY.txt](LICENSE-SERTAY.txt) dosyasında yer alır. Devralınan kodun hakları ilgili hak sahiplerine aittir. Üçüncü taraf lisansları [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) dosyasındadır.
=======
## Lisans / License

SertAy'ın lisanslama yetkisine sahip olduğu Musick katkılarının koşulları [LICENSE-SERTAY.txt](LICENSE-SERTAY.txt) dosyasında yer alır. Devralınan kodun hakları ilgili hak sahiplerine aittir. Üçüncü taraf lisansları [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) dosyasındadır.

[LICENSE-SERTAY.txt](LICENSE-SERTAY.txt) covers contributions that SertAy is authorized to license. Rights to inherited code remain with their respective holders. See [THIRD-PARTY-NOTICES.md](THIRD-PARTY-NOTICES.md) for third-party licenses.
>>>>>>> 627ac7b69e8348a81db28b94ad57780b9ba18370

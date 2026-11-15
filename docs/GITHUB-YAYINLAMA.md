# GitHub'a hazırlama — Musick by SertAy

Kaynak ZIP'inin içindeki `musick` klasörü temiz depo içeriğidir. Bu klasörün içindekileri `sertay1/musick` deposuna yükleyin. `.git`, kişisel hesap ayarları, çerezler, veritabanları, özel anahtarlar, `node_modules`, derleme çıktıları ve araç önbellekleri pakete dahil değildir.

## Kaynak ve EXE ayrı tutulur

- **Depoya:** `src/`, `build/`, `scripts/`, `design/`, `docs/`, `.github/`, `.config/`, `.nuke/`, çözüm ve kök yapılandırma dosyaları.
- **GitHub Release v1.0.0 eklerine:** `Musick-Setup.exe`, `MusicBot-1.0.0-full.nupkg`, `releases.win.json`, `RELEASES`, `SHA256SUMS.txt` ve isteğe bağlı `Musick-1.0.0-Source.zip`.
- EXE ve kaynak ZIP'ini depo dosyaları arasına koymayın; büyük dosyaları Release eki olarak paylaşın.

Release başlığı `Musick 1.0.0 — by SertAy`, etiketi `v1.0.0` olabilir. Güncelleme kontrolü `sertay1/musick` deposunun son kararlı Release'ini okur. Kurulum dosyasının adı `Setup.exe` ile bitmeli ve GitHub dosya özeti SHA-256 olmalıdır.

## Tekrar derleme

Windows'ta .NET 9 SDK ve Node.js kurulu olmalıdır:

```powershell
powershell -ExecutionPolicy Bypass -File scripts/Package-Release.ps1 -Version 1.0.0
```

GitHub Actions manuel olarak da çalıştırılabilir. Workflow testlerden sonra kurulumu artifact olarak hazırlar. `v*` etiketi gönderildiğinde yayımlanmayı bekleyen bir taslak Release oluşturur. Bu hazırlık sırasında GitHub'a kaynak veya Release gönderilmez.

## Yapılandırma ve sürüm

Hesap bilgileri kullanıcı tarafından Ayarlar'dan girilir. Google/Spotify/Kick istemci kimlikleri bir yapımcı etiketi değildir; çalışan entegrasyonların kodu yeniden markalama sırasında değiştirilmez. Kaynak paketinde kişisel oturum verisi bulunmaz.

Bu dağıtımda sürüm 1.0.0'a sıfırlanmıştır. Önceden kullanılan 1.2.x/1.3.x paketleri, otomatik güncelleme karşılaştırmasında daha yüksek numaradır; 1.0.0'a geçiş için kurulum EXE'sini elle çalıştırın. Kaldırma yaparsanız `%AppData%/MusicBot` kullanıcı verilerini silmeyin.

Alternatif tasarım `design/console-concept/index.html` dosyasıdır. Çalışan uygulamaya uygulanmamıştır. Sayfayı tarayıcıda açıp öneriyi değerlendirebilirsiniz.

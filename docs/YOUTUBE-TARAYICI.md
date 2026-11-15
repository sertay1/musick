# YouTube bağlantısı — ortak Musick istemcisi

Kullanıcı Ayarlar → Hesaplar → YouTube ile Oturum Aç düğmesine basar. Varsayılan tarayıcıda kendi hesabını seçer ve izin verir. Kullanıcıya Client ID, Client Secret veya Cloud projesi sorulmaz. Halka açık müzik oynatma hesap bağlantısından bağımsızdır.

## Yayıncı için bir kez yapılacak kurulum

SertAy'a ait Google Cloud projesinde YouTube Data API v3 etkinleştirilir, Musick adı ve destek bilgileri yapılandırılır, Desktop app türünde OAuth istemcisi oluşturulur. İndirilen installed JSON'u yerelde şu komutla aktarın:

```powershell
./scripts/Configure-GoogleOAuth.ps1 -CredentialsPath 'C:/yerel/istemci.json'
```

Bu komut src/MusicBot.Web/google-oauth.desktop.json dosyasını oluşturur. Dosya GitHub'a ve kaynak ZIP'ine dahil edilmez; derleme ve kurulum paketine kopyalanır. Yeni bir bilgisayarda kaynak kodundan kendi dağıtımını derleyen geliştirici bu yapılandırmayı ayrıca sağlamalıdır. Eski Google kullanıcı ayarları ortak istemciyi seçmek için kullanılmaz. İstemci eksikse bağlantı düğmesi kapalıdır, müzik oynatma sürer.

Masaüstü OAuth istemcileri public client sayılır: kurulumla dağıtılan client_secret gizli tutulabilen bir sunucu anahtarı değildir. Güvenlik PKCE S256, tek kullanımlık süreli state ve her kullanıcının cihazında DPAPI ile saklanan özel erişim/yenileme belirteçleriyle sağlanır. Web uygulaması istemci sırrını bu yapıya koymayın.

Dönüş adresi http://127.0.0.1:3050/api/auth/google/callback; kapsam youtube.force-ssl olup liste ve beğeni işlemleri için kullanılır. Google izin ekranındaki kapsam açıklaması daha geniş yetkiler içerebilir; Google'ın scope tanımı budur.

Google Cloud'da test kullanıcıları, yayın durumu ve gerekli doğrulama işlemleri yayıncı tarafından tamamlanır. Test modunda yenileme belirteçleri 7 gün sonra sona erebilir. Kendi istemcimizi eklemek Google doğrulamasını tamamlamaz ve uyarının kalkmasını garanti etmez. Genel dağıtımdan önce Google doğrulaması tamamlanmalıdır.

Eski geliştiricinin istemcisi ve relay servisi kullanılmaz. Tarayıcı çerezleri okunmaz. Gömülü oynatıcı hata verdiğinde yerel ses hazırlama yolu denenir; erişim kısıtlarını aşmayı garanti etmez.

Resmi kaynaklar: [Masaüstü OAuth ve PKCE](https://developers.google.com/identity/protocols/oauth2/native-app), [YouTube yetkilendirmesi](https://developers.google.com/youtube/data/guides/authentication), [Test modu](https://support.google.com/cloud/answer/15549945).

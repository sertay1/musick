# Musick 1.0.0 — Ponytail denetimi düzeltmeleri

- Spotify'a önceden gönderilen izleyici isteği artık görünür biçimde korunur. Çalmaya başlayana kadar silme veya sıra değiştirme engellenir; böylece Musick ile Spotify kuyrukları ayrılmaz. Kesin reddedilen hazırlıkta koruma kaldırılır. Belirsiz ağ sonucu tekrar gönderilmez. Koruma yeniden başlatmada korunur.
- Otomatik adaylar arka plan önceliğinde kalır. Yeni izleyici isteği öne geçebilir. Otomatik/arka plan adayları için yalnızca metadata hazırlanır; Spotify'a geri alınamayan erken kuyruk ekleme yapılmaz.
- YouTube şarkı doğrulaması önbellekten ve yerel yedek oynatma yolundan atlanamaz.
- Çoklu liste/albüm isteklerinde rolün kişi başı sınırı her parçada uygulanır.
- Güncelleme kurucusu ana süreç kapanmadan başlamaz. İndirme geçici dizindedir. WebView profili kurulum klasöründen ayrılır, pencere kapanırken WebView serbest bırakılır.
- Ayar aktarımı veritabanı işlemi ve dosya geri alma ile korunur. Kalıcı yazma başarısızsa önceki dosyalar ve komut belleği geri yüklenir. Kayıt sonrası donanım/arayüz uygulama hatası başarıyı başarısız aktarım gibi göstermez, uyarı döner.
- Kütüphane özeti toplu sorgulanır; kapaklar için liste başına en fazla dört farklı görsel okunur.

Doğrulama: tam otomatik test çalıştırmasında 399 test geçti; son eklenen aktarım sonrası hata testi ayrıca çalıştırıldı. React üretim derlemesi ve Windows x64 yayını başarılı. Gerçek hesaplarla uzun Spotify/YouTube geçiş oturumu ve Windows üzerinde kurulum/güncelleme henüz canlı denenmedi.

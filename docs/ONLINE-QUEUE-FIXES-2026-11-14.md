# Musick 1.0.0 — çevrimiçi YouTube ve kuyruk düzeltmeleri

Bu not önceki Ponytail raporundaki erken Spotify hazırlık kilidi ve katı şarkı/sanatçı filtresinin yerini alır.

- YouTube kabulü: yayıncının tercihiyle YouTube Müzik kategorisi yeterli. Normal YouTube, YouTube Music ve kısa bağlantılar aynı kimliğe çözülür. Canlı/yaklaşan yayınlar reddedilir. Başlık veya URL alan adı kategori kanıtı sayılmaz.
- Önce dosya indirmeden video metadata okunur; gerekirse yt-dlp yalnızca metadata için kullanılır. Gömülü YouTube oynatıcı doğrudan çevrimiçi çalar. Eski dosya önbelleği veya oynatıcı hatası yerel ses indirmesini tetiklemez. Spotify etiketli istekler de dolaylı YouTube indirmesi başlatamaz.
- Gömülü oynatmaya kapalı/erişilemez video hata bilgisiyle durur; eski parça geri yüklenmez veya sessizce rastgele bir Spotify şarkısına geçilmez. Kullanıcı yeniden deneyebilir ya da atlayabilir.
- Cooldown kontrolü kayıt yapmaz. Başarılı sonuçtan sonra kaydedilir; aynı izleyicinin eşzamanlı komutları sırayla işlenir, farklı izleyiciler birbirini bekletmez. Chat atlama komutu geçişi doğrulanmadan başarılı sayılmaz. Boş başarı şablonu sessiz kalır.
- Otomatik havuz normal bekleyen sıraya önceden aday koymaz. Yalnızca çalan parça, izleyici istekleri ve arka plan listesi bittiğinde kullanılır. Geç gelen havuz sonucu yeni isteği veya yeni çalmaya başlamış parçayı ezemez. Önceki sürümden kaydedilen spekülatif adaylar yüklemede elenir.
- Musick sırası esas alınır. Spotify yerel kuyruğuna erken ekleme ve sıra kilidi kaldırılmıştır; bekleyen ikinci şarkı silinebilir/taşınabilir. Spotify seçimi güncel katalog kimliğini Spotify kuyruğunda doğrular ve yalnızca seçilen parça ilk sıradaysa tek atlama gönderir. Doğrudan URI gövdesiyle bağlam değiştirme masaüstünde gerileme yarattığından kaldırılmıştır. Belirsiz yanıt tekrar eden oynatma/atlama spamına dönüşmez. Şarkı sınırındaki geçiş planı güncel sırayı kullanır.
- Spotify'ın belgelenmiş API'si yerel kuyruğu silme/yeniden sıralama sağlamadığı için sürekli toplu ve iki yönlü yerel kuyruk eşitlemesi yapılmaz. Karışık Spotify/YouTube sırası Musick tarafından yönetilir.
- Servis/doğrulama/kuyruk hataları, kapalı kaynak ve liste istekleri, bağlantı uyarıları ve liste/albüm sonuçları Komutlar ve Yetkilerde düzenlenebilir. Yeni anahtarlar eski dosyalara eklenir; mevcut boş şablonlar boş kalır. {error} hata ayrıntısıdır.

Doğrulama: 405 otomatik test geçti; son dolaylı indirme engeli ilgili alt testlerde ayrıca doğrulandı. React üretim derlemesi ve Windows x64 yayını başarılı. Gerçek doğrulayıcıyla 9bZkp7q19f0 ve dQw4w9WgXcQ kabul edildi, müzik dışı YE7VzlLtp-4 reddedildi; ses indirmesi başlamadı. Canlı Kick mesajı gönderilmedi. Gerçek Spotify/YouTube ile uzun süreli sesli geçiş ve Windows kurulum akışı burada canlı doğrulanmadı.

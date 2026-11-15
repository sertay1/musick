# Musick 1.0.0 — kitaplık ve oynatma kontrolleri

- Chat liste aktarımı artık sabit 25 parçada kesilmez. Liste önizlemesi de 200 kayıtla sınırlanmaz. Rol bazındaki gerçek kuyruk limitleri, yinelenen parçalar ve engeller geçerlidir.
- Spotify eşitlemesi yalnızca bağlı hesabın sahibi olduğu listeleri alır ve bütün sayfalarını okur. Başarısız yeni aktarımlar boş liste yaratmaz; önceki dolu listeler korunur. Önceki eşitlemeden kalan boş, etkin olmayan, hatalı üçüncü taraf liste kayıtları temizlenir.
- Spotify API erişimi reddedilen bir listenin tamamı uygulama tarafından zorla okunamaz. Tamamlanmamış 50 kayıtlık web önizlemesi tam liste diye kaydedilmez. Gerekirse Spotify içinde parçaları kendi listenize kopyalayıp bu listeyi aktarın.
- Her parçanın kendi kapak bilgisi korunur. Eksik kapak için liste görseli bütün şarkılara kopyalanmaz; arayüz parça kimliği üzerinden kapak çözümleyebilir.
- Bütün paylaşılan YouTube şarkı arama yolları sonuçları müzik kategorisiyle doğrular. Canlı ve yaklaşan yayınlar kabul edilmez. Metin aramaları sonuç sayısıyla sınırlıdır; bütün kayıtları alma liste aktarımı içindir.
- Ayarlar → Sistem → Kara liste. Mevcut çalan veya bekleyen şarkının engelleme düğmesi engelliyken kırmızıdır; aynı düğmeyle geri alınır. Bekleyen satır geri alma için görünür kalır ve hâlâ engelliyse sıra ilerlerken atlanır. Engellenen mevcut parça kendiliğinden kesilmez.
- Kara liste kayıt kimliği bazındadır. Spotify ile YouTube arasında ortak şarkı kimliği eşleştirmesi bu sürümün kapsamına alınmadı.
- Ayarlar → Oynatıcı ve ses → Spotify’ı aç. Musick seçilmiş bir parçayı başlatırken boş oturumda kullanılabilir Spotify cihazını oynatmadan etkinleştirmeyi dener. Spotify kurulu, hesap bağlı ve oynatma API izinleri geçerli olmalıdır.
- Spotify hedefi eski sıra kayıtlarının arkasında kaldığında sınırlı bir kurtarma yolu kullanılır. En fazla dört eski kayıt, Windows Spotify ses oturumu geçici olarak susturularak geçilebilir; hedef doğrulandıktan sonra oynatma sürdürülür. Önceden sessize alınmış oturumun sessizliği korunur. Daha uzun veya işlem sırasında değişen kuyrukta körlemesine atlama yapılmaz. Kurtarma normal geçişten uzun sürebilir.
- Oynatıcıdaki işlevsiz sürekli karıştırma düğmesi kaldırıldı. Kuyruktaki çalışan tek seferlik Karıştır eylemi duruyor.
- Spotify/YouTube otomatik ortak LUFS normalizasyonu eklenmedi. Spotify kendi uygulamasından çaldığı için Spotify’ın ses normalizasyonu ve Musick’in mevcut YouTube ses düzeltmesi ayrı çalışır.

## Doğrulama

- Tüm çevrimdışı regresyon paketi: 434 kontrol başarılı; eklenen iptal sırasında sessizliğin geri yüklenmesi kontrolü de başarılı.
- Gerçek Spotify masaüstünde eski sıra kayıtları üzerinden hedef parçaya geçiş doğrulandı. Testte Spotify duraklatılmışken Sonraki komutunun yeniden oynatmayı başlattığı görüldü; geçici Windows ses susturması bu davranış için eklendi. Dört kayıtlık kurtarma örneği yaklaşık 17,6 saniye sürdü.
- Gerçek YouTube metadata kontrolü: 9bZkp7q19f0 müzik kaydı kabul edildi; kategori filtreli arama dört kapaklı sonuç döndürdü (yaklaşık 1 saniye). Hesap bağlantısı, ses/video indirme veya oynatma başlatılmadı.
- React/TypeScript üretim derlemesi başarılı.
- Gerçek Kick mesajı gönderilmedi. Spotify’ın tamamen boş oturumdan ilk açılışı canlıda doğrulanmadı; bu yol sahte API ve cihaz etkinleştirme testleriyle kontrol edildi. Uzun süreli canlı yayın ve tüm ses cihazları denenmedi.


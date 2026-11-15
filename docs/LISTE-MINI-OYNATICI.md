# Herkese açık listeler, sıra temizleme ve küçük oynatıcı

Sürüm: 1.0.0 — by SertAy.

## Spotify

Herkese açık olmak Spotify Web API üzerinden tam liste içeriğine erişim izni sağlamıyor. API erişimi reddedilirse uygulama herkese açık embed içeriğini, normal liste sayfasındaki toplam kayıt sayısıyla karşılaştırır. İçeriği tam doğrulanabilen liste aktarılır. Toplam sayı bilinmiyorsa veya önizleme eksikse eksik içerik tam liste diye kaydedilmez. Bu ortak yol chat ve uygulama içi aktarımda kullanılır; yalnızca size ait listeleri eşitleme davranışı korunur.

Paylaşılan `2BiGlJ8Jqa1mtCFY491sFb` listesinde canlı kontrolde toplam 197 kayıt, önizlemede 100 parça vardı. Bu listenin 197 parçasını mevcut erişimle tam almak doğrulanamadı. Tam aktarım için parçaları Spotify içinde kendi listenize kopyalayabilir veya sahibinden ortak düzenleme izni alabilirsiniz. Kaynak: https://developer.spotify.com/documentation/web-api/reference/get-playlists-items

## Temizle

Yayın masası düğmesi hem normal istekleri hem liste kaynaklı bekleyen kayıtları hem de döngüsel çalma sırasını temizler. Mevcut çalan parça, kitaplıktaki listeler ve otomatik kuyruk havuzu silinmez. Etkin kitaplık listesi devre dışı bırakılır; uygulama yeniden başladığında temizlenen sıra geri kurulmaz. Otomatik kuyruk açık bırakıldıysa mevcut parça bittikten sonra havuzdan tekrar parça seçilebilir.

## YouTube

Oynatıcı başlığındaki küçült/büyüt düğmesi video alanını 320×200 ile 200×200 arasında değiştirir. Boyut tercihi saklanır. İframe aynı kalır; bu işlem şarkıyı yeniden yüklemez. YouTube oynatılırken görünür video alanını tamamen kapatmak eklenmedi. Kaynak: https://developers.google.com/youtube/terms/required-minimum-functionality

## Kontroller

- 84 ilgili otomatik regresyon kontrolü: kuyruk davranışları, kitaplık verilerinin korunması, tam/eksik halka açık liste aktarımı, 25/50/90 kaydın aktarılması ve 197/100 eksik içeriğin reddi.
- İzole tarayıcı kontrolü: gerçek video veya hesap açılmadan 320×200 → 200×200 → 320×200; oynatıcı örneği ve yükleme sayısı değişmedi, yeniden açılışta tercih korundu.
- Paylaşılan 197 kayıtlık liste uygulamanın ortak önizleme servisiyle canlı, anonim metadata okumasıyla doğrulandı. Hesaba giriş, chat veya oynatma başlatılmadı.
- React/TypeScript üretim derlemesi başarılı.
- YouTube boyut değişimi gerçek video yayını sırasında ve uzun yayın boyunca denenmedi; Spotify API sahiplik kısıtı kaldırılmadı.

# Musick 1.0.0 — overlay koleksiyonu

## Kullanım

Overlayler / Görünüm ve OBS bölümünde önce Stüdyo, Plak, Yayın bandı, Kaset veya Tipografi tasarımını seçin. Renk paleti ayrı bir seçimdir; bütün tasarımlar koyu, açık, cam, minimal ve neon paletlerle kullanılabilir. Önizlemeler örnek şarkılar kullanır; gerçek kuyruk veya oynatma değiştirilmez.

Renkler, köşe yuvarlaklığı, başlık boyutu, ölçek, opaklık, gösterilecek satır sayısı, kapak, isteyen kişi, ilerleme, albüm arka planı ve animasyonlar ayarlanabilir. Ayarlar otomatik kaydedilir ve açık overlaylere iletilir. Seçim, ayar yedeğiyle taşınabilir. Önceki ayarlar yeni tasarım alanı olmadan da okunur.

Altı kaynak adresi korunur: yatay/dikey Şu An Çalıyor, yatay/dikey Kuyruk ve yatay/dikey birleşik oynatıcı. Yeni önerilen OBS boyutları bağlantı kartlarında gösterilir; özellikle büyük kapaklı dikey görünümlerde kaynak yüksekliğini artırmanız gerekebilir.

Yayın masasında liste içe aktarırken isteyen adı sorulmaz. Kayıtlı Kick hedef kanalı otomatik kullanılır; ayrıca tanımlanmış bot hesabı hedef kanal adının yerine geçmez. Kanal bilgisi bulunamazsa Yayıncı yazılır. Chat üzerinden gelen gerçek kullanıcı istekleri kendi isimleriyle kalır.

## Doğrulama

- 11 backend kontrolü başarılı: beş tasarımın kalıcılığı, özelleştirmelerle birlikte kaydedilmesi, eski ayar uyumluluğu, ayar yedekleme ve kayıtlı hedef kanalın 90 şarkılık listeye otomatik atanması.
- İzole tarayıcıda 30 tasarım / yerleşim birleşimi ve 30 özelleştirme birleşimi kontrol edildi. Yedi piksel köşe, 18 piksel başlık, özel yazı/zemin rengi, .65 opaklık ve kapatılan kapak/ilerleme/isteyen alanları gerçek hesaplara bağlanmadan doğrulandı.
- Kuyruk limitinin dört kuyruk görünümünde uygulanması; geçiş boşluğunda son şarkının korunması ve bu seçenek kapalıyken gizlenmesi kontrol edildi. Dikey birleşik görünümün eksik satır seçicisi ve yeni şarkı geldikten sonra çalışabilen eski gizleme zamanlayıcısı düzeltildi.
- React / TypeScript üretim derlemesi ve masaüstü dağıtımı kontrol edilir. Canlı Kick mesajı veya gerçek Spotify/YouTube oynatma başlatılmaz.
- Uzun süreli OBS yayınında test yapılmadı. OBS kaynak boyutunu seçilen tasarıma ve ölçeğe göre ayarlayın.

## Tasarım referansları

Yerleşim çeşitliliği ve ayar önizlemesi için Nowify ve Desk FM örnekleri incelendi; bu projelerden kod veya görsel alınmadı.

- https://github.com/KelvinPH/Nowify
- https://github.com/pracheersrivastava/desk-fm

SignalR tarayıcı kitaplığı uygulamanın mevcut @microsoft/signalr bağımlılığından paketlendi. Üçüncü taraf lisansı ilgili dosyayla dağıtılır.

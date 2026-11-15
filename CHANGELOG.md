# Sürüm notları

## 1.0.0 — Musick by SertAy

### Kaset ve birleşik oynatıcı yerleşimleri

- Kasetin alt penceresi kapak görselini gösterecek biçimde yenilendi. Kapak animasyonları açıkken oynatma durumunu izleyen iki döner makara eklendi.
- Birleşik yatay/dikey kaynaklarda kapak, plak ve kaset alanlarının boyut, oran ve hizaları düzenlendi.
- Tipografi seçimden kaldırıldı; eski Tipografi ayarları ve bağlantıları Kompakt görünümüne döner.

### Overlay tasarımları ve bağımsız efektler

- Stüdyo, Plak, Yayın bandı, Kaset ve Tipografi yeniden çizildi; dokuz tasarımın tamamı seçim alanında doğrudan görünür. Ortak kuralların tasarımı bastıran önceliği düzeltildi.
- Altı yeni palet; ölçek kontrolü kaldırıldı. Arka plan opaklığı artık metni ve albüm kapağını soldurmaz.
- Neon ayrı anahtar, renk, parlama ve hız kontrolleriyle her yerleşim/palette kullanılabilir. Sabit, Kalp atışı, Nefes, Renk döngüsü, Çevre turu, Titreşim. Renk döngüsü kapağa filtre uygulamaz.
- Dört kenarda gölge/neon boşluğu, ayrı gölge anahtarı ve yoğunluğu. Mevcut OBS kaynaklarının boyutları menüdeki yeni değerlerle güncellenmelidir.
- Yatay Klasik dahil bütün katmanlarda albüm arka planı ortak uygulanır. İnce bandın ilerleme çubuğu kutu sınırları içinde tutulur.

### Canlı overlay önizlemesi

- Overlay düzenleyicisi, ana arayüzün mevcut canlı şarkı ve kuyruk bilgisini kullanır. Kapak, isteyen, süre ve duraklatma durumu önizlemeye yansıtılır; ek Spotify isteği veya hub bağlantısı açılmaz.
- Gerçek şarkı ve sıra yoksa örnek görünüm kullanılır. Kuyrukta şarkı varken çalan şarkı yoksa eski örnek şarkı korunmaz.

### Uygulama içi önizleme düzeltmesi

- Ana arayüzün CSP frame-src kuralına aynı kaynak izni eklendi. Böylece kendi overlay sayfaları önizleme panelinde açılır; YouTube gömülü oynatma izni ve diğer güvenlik sınırları korunur.
- Güvenlik başlıklarını doğrulayan regresyon testi eklendi. Altı önizleme, gerçek güvenlik ara katmanı ve derlenmiş arayüzle hesaplara bağlanmadan doğrulandı.

### Overlay düzenleyici

- Tek büyük örnek önizleme; içerik/yerleşim, tasarım ve kişiselleştirme adımları. Görünüm, İçerik ve Hareket ayarları ayrı bölümlerde.
- Kompakt kart, Albüm kartı, İnce bant ve önceki Klasik görünüm. Önceki beş tasarım Diğer tasarımlar altında korunur.
- Altı kaynak için seçili görünüme ait tek OBS bağlantısı, boyutlar, koyu/açık/şeffaflık önizleme zemini ve kopyalama hata kurtarması.
- Dikey birleşik katmanın sırası yalnızca gelecek şarkıları gösterir; çalan şarkı üstte korunur.
- Kompakt kartın bazı CSS oranları MIT lisanslı itshypax/NowPlaying kaynağından uyarlanmıştır; lisansı dağıtıma eklenmiştir. Ticari örneklerin kodu veya görselleri paketlenmez.

### Overlay koleksiyonu ve yayıncı adı

- Yayın masası liste içe aktarma formundaki İsteyen alanı kaldırıldı. Bağlı Kick kanalının kayıtlı adı sunucuda otomatik kullanılır; bot hesabı hedef kanalın yerine geçmez. Kanal yoksa Yayıncı etiketi kullanılır.
- Stüdyo, Plak, Yayın bandı, Kaset ve Tipografi tasarımları; altı yatay/dikey overlay görünümünde uygulanır. Tasarım ile renk paleti ayrı seçilir.
- Uygulama içinde örnek şarkılı beş tasarım önizlemesi. Seçim ve özelleştirmeler otomatik kaydedilir ve açık overlaylere gönderilir.
- Köşe, renkler, başlık boyutu, opaklık, ölçek, kapak, isteyen kişi ve ilerleme anahtarları tasarımlara uygulanır. Dikey birleşik overlayin kuyruk limiti düzeltildi.
- Geçiş boşluğundan sonra yeni şarkıyı gizleyen gecikmiş zamanlayıcı iptal edilir. Varsayılan son şarkıyı koruma davranışı sürer.
- Overlay SignalR tarayıcı kitaplığı yerel dağıtıma alındı; harici font yüklemesi kaldırıldı.

### Herkese açık listeler ve küçük YouTube oynatıcısı

- Spotify API liste erişimini reddettiğinde, herkese açık önizleme toplam kayıt sayısıyla karşılaştırılır. Tam içerik doğrulanırsa aktarılır; eksik önizleme tam liste gibi kaydedilmez. Sabit 50 kayıt varsayımı kaldırıldı.
- Yayın masası Temizle düğmesi liste kaynaklı şarkılar ve döngüsel liste dahil bütün bekleyen sırayı temizler. Çalan parça ve kitaplıktaki kayıtlar korunur; temizlenen liste yeniden açılışta etkinleşmez.
- YouTube oynatıcısında 320×200 / 200×200 görünür video alanı arasında küçültme/büyütme. Oynatıcı yeniden oluşturulmaz, parça yeniden yüklenmez; tercih saklanır. Ses sürerken videoyu gizleme eklenmedi.

### Kitaplık ve oynatma kontrolleri — 14 Kasım

- Chat liste aktarımındaki 25 ve liste önizlemesindeki 200 kayıt sınırları kaldırıldı; rol limitleri, tekrar ve engel kontrolleri korunur.
- Spotify eşitlemesi yalnızca liste sahibinin hesabına ait listeleri alır; başarısız aktarımlar boş liste oluşturmaz. Mevcut dolu listeler korunur.
- Parçaya özel kapaklar korunur; Spotify liste kapağı eksik parça görseli yerine kullanılmaz.
- YouTube arama sonuçları müzik kategorisine göre doğrulanır; video ve canlı yayınlar arama listesine karışmaz.
- Ayarlar → Sistem altında kara liste ve aynı düğmeyle engeli kaldırma; engellenen kayıtlar Musick sırasında ve döngüsel listelerde atlanır.
- Ayarlar → Oynatıcı altında Spotify'ı açma; boş oturumda kullanılabilir cihazı duraklatılmış biçimde devralma.
- Spotify hedefi eski yerel sıra kayıtlarının arkasında kaldığında en fazla dört eski kayıt için kontrollü kurtarma; Windows Spotify ses oturumu geçişte susturulur ve eski sessiz durumu korunur. Onaylanmayan atlama tekrar gönderilmez.
- Çalışmayan oynatıcı karıştırma modu kaldırıldı; kuyruktaki tek seferlik Karıştır eylemi korundu.

- Dağıtım sürümü 1.0.0; uygulama ve kurulum yapımcısı SertAy.
- Mevcut arayüz, oynatma, kuyruk ve komut işleyişi korundu.
- Alternatif konsol tasarımı ayrı bir önizleme olarak eklendi.
- GitHub kaynak paketi, derleme talimatları ve kurulum hazırlandı.

### Kuyruk ve oynatıcı düzeltmeleri — 13 Kasım

- Spotify masaüstü uyumluluğu için doğrulanmış albüm bağlamı ve tam parça konumu kullanılır; Musick ilerletirken Spotify tekrar modu geçici olarak yönetilir ve önceki ayar geri yüklenir.
- Belirsiz geçişlerde hedef tek kayıt olarak korunur; geç gelen gerçek oynatma bilgisi ek çalma isteği göndermeden kabul edilir.
- Açılışta eski kuyruk kopyaları temizlenir; otomatik havuz kara listeye ve kapalı kaynaklara uyar.
- Virgül/boşlukla çoklu takma ad; eski düzenleyicide silinen ve boş bırakılan yanıtların sessiz kalması.
- YouTube için kalıcı ses dengesi; canlı kuyrukta kara liste eylemi; arka plan listesi dahil karıştırma.
- Spotify üçüncü taraf liste içeriğine erişimi reddederse mevcut kitaplık korunur ve kısıt açıklanır.

### Güncel düzeltmeler

- Spotify / YouTube için varsayılan tarayıcıdan giriş; YouTube OAuth ve resmi liste / beğeni API akışı.
- YouTube 150 hata bildirimi ve yerel oynatma yedeği; boşta iframe ve gereksiz paralel indirme yükü kaldırıldı.
- Spotify / YouTube için ortak oylama / AFK başlangıcı ve gecikmiş sonuçların güvenli iptali.
- API / oturum bilgileri hariç ayar yedekleme; otomatik Kick bağlantısı; genişletilen logo paneli ve seçilen görselin başlık / görev çubuğu ikonu.

- Otomatik kuyrukta başarısız Spotify geçişinin aynı şarkıya kilitlenmesi giderildi; sınırlı yeniden deneme ve havuz şarkılarını geçici bekletme.
- Komutları JSON ile içe/dışa aktarma; yeşil/kırmızı rol izinleri.
- Şeffaf Windows simgesi, daha büyük logo, başlık çubuğu çizgisi ve belirgin kaynak/kullanıcı/bağlantı rozetleri.

- Serbest metin şarkı isteklerinde çoklu aday ve başlık/sanatçı eşleştirmesi.
- Büyütülen logo, okunaklı görev çubuğu simgesi ve üstte by SertAy.
- Açık/koyu temayla bütünleşen yerel başlık çubuğu.
- Ayrıntılar: docs/SON-DUZELTMELER.md.


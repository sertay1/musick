# Musick 1.0.0 — güncel düzeltmeler

- `by SertAy` üstteki slogan alanına taşındı; alttaki yinelenen etiket kaldırıldı.
- Uygulama içindeki mevcut tam logo büyütüldü.
- Görev çubuğunda daha fazla alan kullanan K / kulaklık simgesi hazırlandı. ICO dosyası 16–256 piksel arasında on çözünürlük içerir; pencere yüksek çözünürlüklü, sistem tepsisi küçük kareyi seçer.
- Başlık çubuğu WPF WindowChrome ile uygulamaya bütünleştirildi; açık/koyu temayı izler. Küçültme, büyütme/geri yükleme ve kapatma düğmeleri Windows sistem komutlarını kullanır.
- Serbest metinle şarkı istenirken ilk Spotify sonucunu doğrudan almak yerine on adayın başlık/sanatçı uyumu karşılaştırılır. YouTube metin aramasında da aynı denetim uygulanır.
- Türkçe karakterler, kelime ayrımları ve uzun kelimelerde tek harflik yazım hataları ele alınır. İstenmeyen karaoke/cover/remix adayları reddedilir; zayıf eşleşme yerine açıklama döner.

Örnek: `!ekle Duman Senden Daha Güzel`. Aynı adlı farklı sanatçı kayıtlarında sanatçıyı belirtmek veya şarkının doğrudan bağlantısını göndermek en kesin seçimdir. Tam Spotify/YouTube bağlantıları metin eşleştirmesine girmez.

Kontrol: 266 çevrimdışı otomatik kontrol; sahte Spotify servisiyle gerçek komut yönlendirmesi, çoklu aday seçimi ve doğrudan bağlantı davranışı. Ayrıca üç gerçek Spotify katalog araması yalnızca okunarak doğrulandı. Canlı oynatma veya Kick sohbetine mesaj gönderilmedi. Başlık çubuğu normal/büyütülmüş durumda ve açık/koyu temada ayrı, hesaplara bağlanmayan bir çizim kontrolünden geçirildi.

Alternatif konsol tasarımı hâlâ uygulamadan ayrıdır; bu güncelleme mevcut arayüz düzenini kullanır. Sürüm numarası isteğe uygun olarak 1.0.0 korunmuştur. Güncel kurulum elle çalıştırılır.

## Başlık, rozetler, komut aktarımı ve otomatik kuyruk

- Başlık çubuğunun altına açık/koyu temayı izleyen ayırıcı çizgi eklendi.
- Üst panel 84 piksele, mevcut tam logo alanı 188 × 78 piksele büyütüldü.
- Görev çubuğu simgesi temiz piksel çizgileriyle vektör olarak yeniden çizildi. Arka plan gerçek alfa şeffaflığına sahiptir; simge kareyi küçük güvenlik payıyla doldurur. 16–256 piksel on ICO karesi ve 1024 piksel PNG kaynak bulunur. Yeniden üretim: `scripts/Generate-AppIcon.ps1`.
- Komutlar & Yetkiler ekranında **Dışa aktar** / **İçe aktar** eklendi. JSON; komutları, takma adları, izinleri, rol profillerini, bekleme sürelerini ve yanıt şablonlarını taşır. Hesap oturumlarını taşımaz. Dosya biçimi/sürümü ve alanlar sunucuda doğrulanır; 2 MB sınırı vardır. Ham `commands.json` dosyaları da kabul edilir; eksik yeni yerleşik komutlar varsayılanlardan tamamlanır. İçe aktarma mevcut ayarların yerini alır ve otomatik kaydedilir.
- Rol izin düğmeleri yeşil İzinli / kırmızı Engelli, ayrıca ✓ / × işaretleriyle gösterilir.
- Bağlantı durumları, kuyrukta isteyen kişi ve Spotify / YouTube kaynakları yeniden belirgin rozetler halinde gösterilir.
- Otomatik kuyrukta doğrulanamayan havuz şarkısının tekrar sıranın başına konması ve bitiş izleyicisinin kilitli kalması düzeltildi. Başarısız havuz şarkısı iki dakika bekletilir; başka adaylar sınırlı sayıda denenir. Tüm denemeler başarısızsa 15 saniye sonra yeniden denenir. Bu işlemler havuzdan şarkı silmez. Chat istekleri hatada korunur; otomatik şarkı temizliği bekleyen bir isteği ileri alıp kaybetmez. Spotify geçiş bayrağı, yerel oynatıcı durdurulurken hata oluşsa da serbest bırakılır.

Kontrol: **277 çevrimdışı kontrol geçti.** 400 şarkılık SQLite havuzu ve sahte Spotify ile manuel atlama, otomatik geçiş, peş peşe doğrulama hatası, aynı servisle yeniden oynatma ve hata sırasında gelen chat isteğinin korunması test edildi. Komut JSON aktarımı ve geçersiz dosya reddi de doğrulandı. Arayüz açık/koyu temada izole verilerle kontrol edildi; canlı Spotify/Kick hesabında oynatma veya mesaj gönderimi yapılmadı. Kurulum paketi Windows x64 ve sürüm 1.0.0 olarak hazırlandı.
## 12 Kasım — son arayüz, bağlantı ve oynatma kontrolleri

- Gönderilen K/kulaklık PNG görseli doğrudan ikon kaynağı olarak seçildi; yeniden çizilmiş farklı simge kaldırıldı. ICO boyutları özgün alfa kanalı korunarak üretildi. Aynı ikon başlık çubuğunun başında da gösterilir.
- Üst logo paneli 108 piksel yüksekliğe çıkarıldı; tam logo 188 × 88 alanda dikey ortalandı. Sol alttaki Sistem bağlı / Bağlantı bekleniyor metni kaldırıldı.
- Spotify OAuth açılışı sistem tarayıcısına taşındı. YouTube için tarayıcı OAuth akışı, süreli tek kullanımlık state, PKCE, token yenileme ve DPAPI saklama eklendi. Google tarafındaki API/izin ekranı gereksinimleri için `YOUTUBE-TARAYICI.md` dosyasına bakın.
- YouTube liste okuma, tüm sayfaları dolaşan resmi API akışını kullanabilir; beğeniler de OAuth ile senkronize edilir. Eski isteğe bağlı çerez oturumları desteklenir, normal girişte WebView2 penceresi açılmaz.
- YouTube 150 hatasında video bilgisi boş olduğu için hatanın sunucuya hiç gönderilmemesi düzeltildi. Gömülü oynatma başarısız olduğunda yerel ses hazırlama denenir. Gecikmiş hazırlama, atlanan veya yeni seçilen şarkıyı değiştiremez. Bilinen gömme kısıtlarında tekrar gereksiz iframe denemesi yapılmaz.
- YouTube API/iframe yalnızca YouTube şarkısı seçildiğinde yüklenir. Normal iframe şarkıları için gereksiz paralel yt-dlp / ffmpeg indirmesi yapılmaz; yerel yedek gerektiren bilinen şarkılar önceden hazırlanabilir.
- Kick otomatik bağlantı anahtarı kaldırıldı. Kayıtlı kanal ve geçerli hesap varsa açılışta bağlantı denenir; eski kapalı otomatik bağlantı bayrağı bunu engellemez.
- Oylama / AFK artık doğrulanmış Spotify, YouTube ve yerel oynatmanın ortak akışında başlar. AFK yalnızca gerçek chat isteklerine uygulanır. Eski oylamanın gecikmiş sonucu yeni şarkıyı atlayamaz; sıradaki kişinin onayı mevcut kişinin AFK kontrolünü yanlışlıkla tamamlayamaz. Özellikler kapatıldığında etkin kontrol iptal edilir. Oylama iptalinde overlay doğru şekilde kapanır; tasarımlar değiştirilmedi.
- Ayarlar → Sistem → **Ayarları yedekle ve taşı** eklendi. Oynatma, kaynak açma/kapama, oylama/AFK, komutlar, rol profilleri, kanal izinleri, overlayler, otomatik mesajlar, yasaklı şarkılar, ses aygıtı/seviyesi ve görünüm taşınır. API anahtarları, istemci sırları, hesap tokenları/çerezleri, çalma listeleri, havuz içeriği ve indirilmiş dosyalar dahil değildir. İçe aktarma öncesi tüm değerler doğrulanır. Başka bilgisayarda bulunmayan ses aygıtı varsayılana döner; açılışı durdurmaz.

Kontrol: **292 çevrimdışı otomatik kontrol başarılı.** Yeni kontroller; OAuth/PKCE ve token yenileme, 50 sınırını aşan YouTube sayfalaması, beğeni istekleri, ayar yedeğinde gizli alanların dışarıda kalması ve dosyadaki API bilgilerinin korunması, Spotify/YouTube oylama ve AFK başlangıcı, eski sonuçların iptali, yerel oynatma yedeği ve bulunamayan ses aygıtı durumlarını kapsar. İzole tarayıcı testinde metadata boşken 150 hatası iletildi; boşta iframe sayısı sıfır kaldı. Arayüz ve yerel başlık çubuğu açık/koyu temada kontrol edildi. Canlı Kick mesajı, gerçek Google yetkilendirmesi veya gerçek video indirme/oynatma testi yapılmadı. Sürüm 1.0.0 korundu.

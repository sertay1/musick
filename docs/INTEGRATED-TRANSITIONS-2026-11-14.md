# Entegre geçişler ve bildirim ayarı — 14 Kasım 2026

## Düzeltilen nedenler
- Spotify durum yanıtı iki saniye önbellekte tutulabiliyordu. Geçiş zamanlayıcısı bu yanıtı yeni ölçülmüş gibi ele alıyordu. PlaybackState artık yakalama zamanını korur; bitiş zamanı örnek yaşına göre hesaplanır. Harici ileri/geri sarma eski zamanlayıcıyı iptal edip yeniden planlar.
- Tekrar-tek-parça kiralaması, geç kalınan geçişte eski parçanın başa dönmesini sağlıyordu. Musick yönetirken tekrar modu kapatılır; kontrol bırakılırken önceki kullanıcı modu geri yüklenir.
- Şarkı sonundaki duraklatma ile kuyruk ilerletme farklı sahipler tarafından yapılabiliyordu. Artık ikisi aynı geçiş kilidi ve SkipPending sahipliği altında yürür. Kaynak değişiminden önce gerçek duraklama doğrulanır; boş durum yanıtı duraklama kanıtı sayılmaz.
- Doğrulama her defasında iki saniye beklemek yerine önce yeni durumu okur; yalnızca sonuç gecikirse sınırlı aralıklarla tekrar okur. Aynı oynatma/atlama işlemi tekrar gönderilmez.
- Spotify aramasından gelen güncel katalog kaydı geçişte tekrar aranmaz. Sıradaki Spotify kaydının metadata bilgisi önceden ısıtılır; bu işlem Spotify kuyruğunu değiştirmez ve Musick'te silme/taşımayı kilitlemez.
- Spotify geçişi tamamlanmadan arayüzde çalıyor gösterilmez. Durum, arama ve kuyruk HTTP/JSON kaynakları okuma tamamlanınca serbest bırakılır.

## Bildirimler
- Pencerenin X düğmesi uygulamayı tepsiye aldığında Windows bildirimi gösterilir. Bildirime tıklamak paneli açar.
- Ayarlar / Sistem bölümündeki Masaüstü bildirimleri anahtarı otomatik kaydedilir ve hemen uygulanır. Eski yedekte alan yoksa mevcut tercih korunur; yeni ayar yedeklerine dahil edilir.

## Doğrulama
- 416 otomatik test geçti. Yeni testler örnek yaşını, zamanlayıcı iptalini, duraklatma/ilerletme sahipliğini ve bildirim ayarının kalıcılığını kapsar.
- Gerçek masaüstü Spotify -> Spotify geçişinin ilk ölçümü 4138 ms idi; bunun bir kısmı sabit doğrulama beklemesi ve metadata sorgularıydı. Yeni hız için ikinci karşılaştırmalı ölçüm, oturum süresi dolmaya yaklaştığından güvenlik kontrolüyle iptal edildi; yeni bir süre iddiası yapılmıyor.
- Ayrı test profilinde gerçek WPF/WebView2, Kestrel, PlaybackSyncService ve SignalR ile Spotify -> YouTube -> Spotify doğal bitiş zinciri doğrulandı. YouTube pozisyonu 2052 ms iken oynuyor raporlandı; bu sırada Spotify duraklamıştı. Dönüşte istenen Spotify kaydı oynuyor olarak doğrulandı.
- Testte iki parçanın sonuna kontrollü ileri sarma kullanıldı; bütün şarkılar baştan sona beklenmedi. Kick, otomatik bağlantı ve ticker hizmetleri başlatılmadı, chat mesajı gönderilmedi. Kullanıcının kalıcı kuyruğu kullanılmadı.
- Uzun yayın oturumu ve Windows bildiriminin işletim sistemi tarafından ekranda teslim edilmesi ayrıca doğrulanmadı.

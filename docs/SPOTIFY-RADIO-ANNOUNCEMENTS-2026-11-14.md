# Spotify radyo anonsları — 14 Kasım 2026

Otomatik kuyruk kapalıyken Spotify'ın kendi çaldığı gerçek kaydın anonsu, önceki yönetilen geçişin hata durumuyla engellenebiliyordu. Çalma ve kuyruk ilerletme için bu hata koruması devam eder; kararlı gerçek oynatma gözlemi ve anons artık bundan bağımsızdır. Musick üzerinden verilen duraklatma isteği korunur.

Harici Spotify kaydı artık başka bir bekleyen/önceki kuyruk öğesinin kimliğini veya isteyenini miras almaz. O eski öğenin kaldırılması, aynı çalan parçanın doğrulamasını sıfırlamaz veya bekleyen doğru mesajı iptal etmez. Gönderim anında gerçek parça değişmişse mesaj yine iptal edilir. İki gözlemli doğrulama, atomik tek-anons koruması ve boş success_no_requester şablonunun sessizliği korunur.

Yeni testler eski kuyruk sahibinin kaldırılmasını, bekleyen mesaj sırasında gerçek parçanın değişmesini, başarısız yönetilen seçim varken radyo oynatmasını ve boş şablonu kapsar. Chat göndericisi testte bellek içindedir; gerçek Kick mesajı gönderilmez. Spotify oynatma motoru ve sıralama davranışı bu düzeltmede değiştirilmez. Gerçek Kick teslimi bu çalışmada canlı denenmedi.

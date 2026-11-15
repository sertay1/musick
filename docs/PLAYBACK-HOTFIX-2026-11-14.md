# Oynatma düzeltmesi — 14 Kasım 2026

Canlı logda Spotify yeni parça için doğrudan /play isteğini kabul ettiği halde önceki kayıt çalmaya devam etti. Bu gerilemeye yol açan URI gövdeli doğrudan başlatma yolu kaldırıldı. Spotify'ın mevcut masaüstü oturumunda kuyruk/atlama işlemi kullanılıyor. Musick'te erken kuyruk ekleme ve silme/taşıma kilidi geri getirilmedi. Başka parçalar hedefe ulaşmak için körlemesine atlanmıyor; belirsiz sonuçta aynı işlem tekrarlanmıyor.

YouTube masaüstü iframe belgesine Google'ın istediği HTTPS uygulama kimliği Referer başlığı ekleniyor. Kimlik Velopack paket kimliği MusicBot'tur; üçüncü taraf site taklit edilmez. Başlık yalnızca gerçek YouTube embed belge adreslerine uygulanır. YouTube 150 yayıncı kısıtlaması için de kullanılabilir; kimlik düzeltmesi bu tür video kısıtlamalarını kaldırmaz.

İlgili transport, iptal, kuyruk sahipliği ve embed kimliği testleri çalıştırıldı. Windows x64 yayın derlemesi doğrulandı. Gerçek hesaplarla sesli geçiş ve YouTube 150 hatasının bu makinede giderildiği canlı doğrulanmadı.

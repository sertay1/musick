# Uygulama güncellemeleri

Musick, `sertay1/musick` deposundaki son yayımlanmış kararlı GitHub Release kaydını kontrol eder. Boş, gizli veya henüz release yayımlanmamış depo normal bir durum olarak gösterilir.

Yeni sürüm için:

1. Projedeki `<Version>` değerini artırın; release etiketiyle aynı sürümü kullanın (örneğin `v1.0.1`).
2. `build.cmd --target Pack --configuration Release --runtime win-x64 --version 1.0.1` ile kurulum paketini oluşturun.
3. GitHub Release içine adı `Setup.exe` ile biten Windows kurulum dosyasını ekleyin. GitHub'ın asset `digest` alanında SHA-256 özeti bulunmalıdır.
4. Release'i yayımlayın. Musick içindeki **Ayarlar → Gelişmiş → Güncellemeleri kontrol et** düğmesi yeni sürümü gösterecektir.

Paketleme için `vpk` aracı gereklidir: `dotnet tool install --global vpk --version 0.0.1298`. Sürüm, Desktop projesindeki Velopack paketine eşittir. Kurulum kendi .NET çalışma zamanını içerir; eksik WebView2 bileşenini kurulum sırasında indirir. Paketleme uygulamanın Velopack başlangıç kancasını doğrular. Kurulum EXE'si tek başına dağıtılabilir; uygulamanın DLL ve web dosyalarını kendisi yerleştirir ve masaüstü/Başlat menüsü kısayollarını oluşturur.

**Güncellemeyi indir ve kur** yalnızca kullanıcı bastığında çalışır. Dosya SHA-256 ile doğrulandıktan sonra kurulum açılır ve Musick kapanır. Sürüm bulunmaması, kurulum eki eksikliği veya doğrulama hatası mevcut uygulamayı değiştirmez. Bu çalışma depoya bir şey yayımlamaz.

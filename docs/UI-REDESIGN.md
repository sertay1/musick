# Musick 1.0.0 arayüz yenilemesi

Dal: `ui-redesign` · Yapımcı: by SertAy · 15 Kasım 2026

## Görsel yön

Profesyonel bir yayın masası: kömür / açık gri yüzeyler, tek lime vurgu, belirgin kapak ve okunaklı listeler. Kullanıcının onayladığı görünüm korunur. Inter Variable gövdeyi, Manrope Variable başlıkları taşır. Fontlar Google Fonts ailesindendir ve Fontsource ile yerel dağıtılır; çevrimiçi font bağlantısı gerekmez.

## Düzen

- Yayın masası: üstte arama ve şimdi çalan bilgisi; altta çalma sırası ve İstek Akışı / Günlük. Masaüstünde iki panel yan yanadır ve kendi içinde kayar. Sıranın sonuna gitmek günlüğü ekrandan çıkarmaz.
- Kütüphane: liste seçimi ve şarkı ayrıntıları, tutarlı arama/aktarma kontrolleri ve klavyeyle liste seçimi.
- Otomatik kuyruk: çalışma durumu ve sayılar üstte, havuza ekleme ile şarkı havuzu ayrı sütunlarda.
- Komutlar: arama/filtre, genişletilebilir düzenleyici, izinli/engelli rol durumları, klavye ile anahtar kullanımı. Takma ad ve yanıt şablonu davranışları korunur.
- Yayın katmanları: masaüstünde sol ayar alanı bağımsız kayar, sağ önizleme görünür kalır. Önizleme genişliğe ve yüksekliğe sığar; dikey tasarım kırpılmaz. OBS ekleme bilgileri kendi kaydırılabilir alanındadır. Yayında kullanılan sekiz overlay tasarımı değiştirilmedi.
- Ayarlar: aynı kontrol dili, temaya uyumlu durum renkleri ve düzenli bölüm/sekme hiyerarşisi.
- Alt oynatıcı: solda parça, ortada oynatma, sağda ses ve cihazlar. Mevcut kontrol fonksiyonları korunur.

Masaüstü önceliklidir. 768px ve 390px uyumluluğu korunur, ancak son düzenlemenin odağı masaüstünde panellerin birlikte görünmesidir.

## Kullanılan paketler

- shadcn/ui / Base UI / base-nova: button, badge, input, skeleton, tabs, alert-dialog. Kurulum Vite rehberine göre yapıldı; mevcut React 19 / TypeScript / Vite yapısı korundu.
- Tailwind CSS v4 + Vite eklentisi. Eski kontrollerin bozulmaması için küresel Preflight uygulanmaz.
- Magic UI: yalnızca NumberTicker; `motion` ile küçük sayı geçişi. Hareket azaltma tercihinde statik sayı kullanılır.
- ReUI: yalnızca ücretsiz MIT Timeline primitive; ücretli blok veya şablon yoktur.
- Fontsource: Inter Variable ve Manrope Variable. Geist kaldırıldı; üçüncü font eklenmedi.
- ESLint + typescript-eslint + react-hooks: sözdizimi, problem kuralları ve hook sırası denetimi. Eski `any` / kullanılmayan değişkenler / bilinçli boş catch davranışları kapsam dışında bırakılmıştır.
- Tanıtım sayfası bulunmadığından Aceternity eklenmedi.

Kurulum kaynakları: [shadcn Vite](https://ui.shadcn.com/docs/installation/vite), [Magic UI NumberTicker](https://magicui.design/docs/components/number-ticker), [ReUI Timeline](https://reui.io/docs/components/base/timeline), [Tailwind Vite](https://tailwindcss.com/docs/installation/using-vite), [Fontsource](https://fontsource.org/docs/getting-started/install). Lisanslar `docs/licenses` içindedir.

## Kontroller

- `npm run lint`: sıfır hata / uyarı.
- `npm run build`: TypeScript ve üretim derlemesi başarılı.
- `scripts/test-workspace-theme.mjs`: sistem teması, kullanıcı tercihi, kalıcılık, geçersiz eski değer ve dinleyici temizliği başarılı.
- Mevcut overlay görünüm / önizleme kontrolleri başarılı.
- Mevcut LocalSecurity, OverlayDesign ve SettingsBackup grupları: 36 başarılı, 0 başarısız.
- Altı ekran × 390 / 768 / 1440 × koyu / açık: 36 ekran görüntüsü. Yatay taşma kontrolleri başarılı. Son masaüstü panel düzenlemesinden sonra iki ilgili ekranın 12 görüntüsü yenilendi.
- Klavye: komut genişletme, günlük sekmesi, onay penceresi ilk odağı, Tab odağının pencerede kalması ve Escape ile iptal / odağın geri dönmesi doğrulandı.
- 390px rol tablosu kendi içinde yatay kayar; sayfayı genişletmez. Cihaz menüsü 390px sınırları içinde kalır.
- Masaüstünde sıra kaydırılınca ana alanın kaymadığı ve günlüğün yerinde kaldığı ölçüldü. Overlay ayarları 947px kaydırılıp yarıçap değiştirildiğinde önizlemenin üst konumu değişmedi. Dikey önizleme kullanılabilir yüksekliğe sığdı.
- 133 korunan Core / servis / controller / statik overlay dosyası ile 8 oynatma/API hook dosyası başlangıç yedeğiyle birebir aynı kaldı.

Ekran görüntüleri ve ölçü raporları teslim klasörünün `Kontroller` dizinindedir. Arayüz tarayıcı testleri, gerçek hesaplara bağlanmayan örnek API ve SignalR verileriyle yapılmıştır. Bu çalışma canlı Kick mesajı, Spotify / YouTube oynatma veya OAuth hesap bağlantısı testi değildir.

## Bilinen sınırlar

Üretim bağımlılıklarında `npm audit --omit=dev` bulgu göstermedi. Geliştirme araçlarında 2 düşük / 10 orta düzey uyarı raporlandı (shadcn CLI ve typescript-eslint alt bağımlılıkları). Kırıcı sürüm zorlaması yapılmadı; uygun üst sürümlerde ayrı araç güncellemesi önerilir. Bunlar uygulamada çalışan CLI süreçleri değildir.

Mevcut SignalR paketinin yorum uyarıları ve WPF tarafındaki önceden var olan async uyarısı oynatma kodu değiştirilerek giderilmedi. Gerçek hesaplarla kısa masaüstü kullanım kontrolü, yeni sürüm kurulduktan sonra ayrıca yapılmalıdır.

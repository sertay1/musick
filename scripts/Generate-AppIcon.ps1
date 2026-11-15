# Convert the chosen source image into native icon sizes without changing its artwork.
Add-Type -AssemblyName PresentationCore,WindowsBase
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))
$inputPath=Join-Path $repo 'src/MusicBot.Desktop/Resources/taskbar-symbol.png'
$source=[Windows.Media.Imaging.BitmapImage]::new();$source.BeginInit();$source.CacheOption=[Windows.Media.Imaging.BitmapCacheOption]::OnLoad;$source.UriSource=[Uri]::new($inputPath);$source.EndInit();$source.Freeze()
function RenderIcon([int]$size){
 $v=[Windows.Media.DrawingVisual]::new();[Windows.Media.RenderOptions]::SetBitmapScalingMode($v,[Windows.Media.BitmapScalingMode]::HighQuality)
 $dc=$v.RenderOpen();$dc.DrawImage($source,[Windows.Rect]::new(0,0,$size,$size));$dc.Close()
 $bmp=[Windows.Media.Imaging.RenderTargetBitmap]::new($size,$size,96,96,[Windows.Media.PixelFormats]::Pbgra32);$bmp.Render($v)
 $encoder=[Windows.Media.Imaging.PngBitmapEncoder]::new();$encoder.Frames.Add([Windows.Media.Imaging.BitmapFrame]::Create($bmp));$stream=[IO.MemoryStream]::new();$encoder.Save($stream);return ,$stream.ToArray()
}
$sizes=@(16,20,24,32,40,48,64,96,128,256);$images=@();foreach($size in $sizes){$images+= ,(RenderIcon $size)}
$writer=[IO.BinaryWriter]::new([IO.File]::Create((Join-Path $repo 'src/MusicBot.Desktop/Resources/icon.ico')))
$writer.Write([uint16]0);$writer.Write([uint16]1);$writer.Write([uint16]$sizes.Count);$offset=6+16*$sizes.Count
for($i=0;$i -lt $sizes.Count;$i++){$d=if($sizes[$i] -eq 256){0}else{$sizes[$i]};$writer.Write([byte]$d);$writer.Write([byte]$d);$writer.Write([byte]0);$writer.Write([byte]0);$writer.Write([uint16]1);$writer.Write([uint16]32);$writer.Write([uint32]$images[$i].Length);$writer.Write([uint32]$offset);$offset+=$images[$i].Length}
foreach($bytes in $images){$writer.Write([byte[]]$bytes)};$writer.Dispose()
Copy-Item -LiteralPath (Join-Path $repo 'src/MusicBot.Desktop/Resources/icon.ico') -Destination (Join-Path $repo 'src/MusicBot.Web/clientapp/public/favicon.ico') -Force

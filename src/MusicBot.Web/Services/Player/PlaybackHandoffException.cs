namespace MusicBot.Services.Player;
public sealed class PlaybackHandoffException(Exception inner) : InvalidOperationException(
    "Spotify duraklatılamadı; diğer kaynağa geçiş durduruldu ve istek korundu. " + inner.Message, inner);

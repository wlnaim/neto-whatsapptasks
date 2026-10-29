namespace WhatsAppTasks.Core;

public sealed class MessageSplitter : IMessageSplitter
{
    public IReadOnlyList<string> Split(string text, int maximumLength = 300)
    {
        ArgumentNullException.ThrowIfNull(text);
        if (maximumLength < 1) throw new ArgumentOutOfRangeException(nameof(maximumLength));
        if (text.Length == 0) return new[] { string.Empty };
        // Avoid overflow for a valid, very large configured limit. A surrogate pair may occupy two UTF-16 units when limit is one.
        var parts = new List<string>();
        for (var index = 0; index < text.Length;)
        {
            var length = Math.Min(maximumLength, text.Length - index);
            // Do not split a surrogate pair: preserve every UTF-16 code unit and its Unicode scalar pairing.
            if (length < text.Length - index && char.IsHighSurrogate(text[index + length - 1]) && char.IsLowSurrogate(text[index + length])) length--;
            if (length == 0) length = Math.Min(2, text.Length - index);
            parts.Add(text.Substring(index, length));
            index += length;
        }
        return parts;
    }
}

public sealed class SystemAsyncDelay : IAsyncDelay
{
    public Task DelayAsync(TimeSpan delay, CancellationToken cancellationToken) => Task.Delay(delay, cancellationToken);
}

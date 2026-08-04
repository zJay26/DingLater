namespace DingLater.Core.Capture.DingTalkDatabase;

public sealed class DingTalkCaptureException : Exception
{
    public DingTalkCaptureException(string errorCode, string message)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public DingTalkCaptureException(string errorCode, string message, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public string ErrorCode { get; }
}

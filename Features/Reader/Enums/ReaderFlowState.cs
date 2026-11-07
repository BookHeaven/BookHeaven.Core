namespace BookHeaven.Core.Features.Reader.Enums;

public enum ReaderFlowState
{
    Uninitialized,
    Initialized,
    WaitingForInitialAssets,
    WaitingForChapterAssets,
    Ready
}

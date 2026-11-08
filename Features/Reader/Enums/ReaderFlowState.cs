namespace BookHeaven.Core.Features.Reader.Enums;

internal enum ReaderFlowState
{
    Uninitialized,
    Initialized,
    WaitingForInitialAssets,
    WaitingForChapterAssets,
    Ready
}

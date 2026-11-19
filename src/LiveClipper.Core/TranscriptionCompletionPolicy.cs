using LiveClipper.Domain;

namespace LiveClipper.Core;

public static class TranscriptionCompletionPolicy
{
    // D6: transcription completion is a notification, not consent to generate or apply edits.
    public static bool ShouldQueueAutoEdit(LiveSettings settings, Clip clip) => false;
}

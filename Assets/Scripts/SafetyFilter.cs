using UnityEngine;

public class SafetyFilter : MonoBehaviour
{
    // Starter list only — conservative, NOT exhaustive. Review with a professional.
    private static readonly string[] crisisPhrases = {
        "kill myself", "killing myself", "want to die", "end my life", "ending my life",
        "hurt myself", "hurting myself", "self harm", "self-harm", "suicide", "suicidal",
        "don't want to be alive", "do not want to be alive", "cut myself", "cutting myself"
    };

    // Review exact wording with Mateo / school crisis protocol before go-live.
    public const string CrisisMessage =
        "It sounds like you may be carrying something heavy, and that matters far more than our conversation right now. " +
        "Please talk with someone you trust — a parent, teacher, or school counsellor — about how you are feeling. " +
        "If you need to speak with someone right away, you can call or text 988, the Suicide and Crisis Lifeline, at any time. You are not alone.";

    public bool IsCrisis(string input)
    {
        if (string.IsNullOrEmpty(input)) return false;
        string lower = input.ToLowerInvariant();
        foreach (var p in crisisPhrases)
            if (lower.Contains(p)) return true;
        return false;
    }
}

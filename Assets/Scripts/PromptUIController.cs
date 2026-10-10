using UnityEngine;
using UnityEngine.UI;
using TMPro;

public class PromptUIController : MonoBehaviour
{
    public Button SubmitButton;
    public AIResponseControllerV2 AiResponseController;

    // Spec 5, Fix 3: the one owner of the idle Record/Submit rule. When assigned,
    // typing greys Record (and enables Submit) and clearing does the inverse, via
    // VoiceRecognitionController.ApplyInputModeButtons — a single source of truth
    // shared with the voice re-enable path. Leave unassigned to keep the original
    // Submit-only greying (no Record toggle).
    [SerializeField] private VoiceRecognitionController voiceController;


    void Start()
    {
        SubmitButton.onClick.AddListener(AiResponseController.OnSubmit);

        // Wire the prompt field for empty-field greying + Enter-to-send.
        TMP_InputField promptField = AiResponseController != null ? AiResponseController.PromptField : null;
        if (promptField != null)
        {
            // Empty-field greying: keep Submit disabled while there's nothing to send.
            // Uses the same IsNullOrEmpty test as OnSubmit, so the button is interactable
            // exactly when a submit would actually fire.
            promptField.onValueChanged.AddListener(OnPromptChanged);
            OnPromptChanged(promptField.text);

            // Enter-to-send: pressing Enter/Done in the field submits, same as the button.
            promptField.onSubmit.AddListener(OnPromptEntered);
        }
        else
        {
            Debug.LogWarning("[PromptUI] AiResponseController.PromptField is not assigned; " +
                             "empty-field greying and Enter-to-send are disabled.");
        }
    }

    private void OnPromptChanged(string text)
    {
        // One source of truth: when the voice controller is wired it owns the
        // whole idle Record/Submit rule (Spec 5, Fix 3). Fall back to Submit-only
        // greying when it isn't assigned, preserving the original behavior.
        if (voiceController != null)
            voiceController.ApplyInputModeButtons();
        else
            UpdateSubmitInteractable(text);
    }

    private void UpdateSubmitInteractable(string text)
    {
        SubmitButton.interactable = !string.IsNullOrEmpty(text);
    }

    // Enter/Done routes through the same OnSubmit as the button, so request-locking
    // and the empty/crisis guards in the AI controller all still apply.
    private void OnPromptEntered(string text)
    {
        AiResponseController.OnSubmit();
    }

    private void OnDestroy()
    {
        if (AiResponseController != null && AiResponseController.PromptField != null)
        {
            AiResponseController.PromptField.onValueChanged.RemoveListener(OnPromptChanged);
            AiResponseController.PromptField.onSubmit.RemoveListener(OnPromptEntered);
        }
    }
}

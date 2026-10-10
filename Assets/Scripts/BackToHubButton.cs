using System.Collections;
using UnityEngine;
using UnityEngine.UI;

// "Back to Hub" (Spec 7). Scene component on the character scene's world-space
// button. Wire the Button's OnClick to OnBackToHub().
//
// Ends the conversation cleanly: if a /converse request is in flight
// (aiController.IsProcessing), it waits for the lock to release before
// loading HubScene, so no request is left dangling when the scene — and the
// coroutines driving the request — are destroyed. ProxyClient's worst case is
// ~47s (3 attempts x 15s timeout + backoff); the 60s failsafe below only
// fires if the lock is somehow stuck beyond that, rather than trapping the
// student in the scene forever.
public class BackToHubButton : MonoBehaviour
{
    [Tooltip("The scene's AIResponseControllerV2 (on the AI Response Controller " +
             "GameObject). Used only to read the request lock.")]
    [SerializeField] private AIResponseControllerV2 aiController;

    [Tooltip("The Back to Hub Button itself — greyed out while waiting for an " +
             "in-flight request to finish.")]
    [SerializeField] private Button backButton;

    [SerializeField] private string hubSceneName = "HubScene";

    private const float FAILSAFE_SECONDS = 60f;

    private bool leaving = false;

    public void OnBackToHub()
    {
        if (leaving) return;
        leaving = true;
        StartCoroutine(LeaveWhenIdle());
    }

    private IEnumerator LeaveWhenIdle()
    {
        if (backButton != null) backButton.interactable = false;

        if (aiController != null && aiController.IsProcessing)
        {
            Debug.Log("[Hub] Back to Hub pressed mid-request — waiting for it to finish.");
            float deadline = Time.realtimeSinceStartup + FAILSAFE_SECONDS;
            while (aiController.IsProcessing && Time.realtimeSinceStartup < deadline)
                yield return null;

            if (aiController.IsProcessing)
                Debug.LogWarning("[Hub] Request lock still held after failsafe window — leaving anyway.");
        }

        // Spec 9, item 2: async + faded, same as the hub -> character direction.
        // The wait-for-the-lock logic above is untouched — this only changes HOW
        // the load happens once we have decided to leave.
        SceneTransition.Load(hubSceneName);
    }
}

using UnityEngine;
using UnityEngine.XR.Interaction.Toolkit;
using UnityEngine.EventSystems;
using System.Collections.Generic;

public class HapticFeedbackOnHover : MonoBehaviour, IPointerEnterHandler
{
    public float hapticIntensity = 0.05f;
    public float hapticDuration = 0.1f;

    private XRInteractionManager interactionManager;

    private void Awake()
    {
        interactionManager = FindObjectOfType<XRInteractionManager>();
    }

    public void OnPointerEnter(PointerEventData eventData)
    {
        if (interactionManager != null)
        {
            var interactors = new List<IXRInteractor>();
            interactionManager.GetRegisteredInteractors(interactors);

            foreach (var interactor in interactors)
            {
                if (interactor is XRBaseControllerInteractor controllerInteractor)
                {
                    controllerInteractor.xrController.SendHapticImpulse(hapticIntensity, hapticDuration);
                }
            }
        } else
        {
            Debug.LogWarning("No XRInteraction Manager found!");
        }
    }

}

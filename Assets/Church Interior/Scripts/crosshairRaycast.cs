using UnityEngine;
using UnityEngine.InputSystem; // Necesario para el nuevo sistema

public class CrosshairRaycast : MonoBehaviour
{
    public Texture2D crosshair;
    private Rect position;

    void OnGUI()
    {
        GUI.DrawTexture(position, crosshair);
    }

    void Update()
    {
        // Centrar la mira
        position = new Rect(
            (Screen.width - crosshair.width) / 2f,
            (Screen.height - crosshair.height) / 2f,
            crosshair.width,
            crosshair.height
        );

        // Verificar si el botón izquierdo del mouse fue presionado este frame
        if (Mouse.current != null && Mouse.current.leftButton.wasPressedThisFrame)
        {
            Vector2 mousePos = Mouse.current.position.ReadValue();
            Ray ray = Camera.main.ScreenPointToRay(mousePos);
            RaycastHit hit;

            if (Physics.Raycast(ray, out hit, Mathf.Infinity))
            {
                InteractiveObject obj = hit.collider.GetComponent<InteractiveObject>();
                if (obj != null)
                {
                    obj.TrigegrInteraction(); // Nota: posible typo → debería ser TriggerInteraction?
                }
            }
        }
    }
}
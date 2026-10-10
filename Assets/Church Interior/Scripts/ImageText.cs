using UnityEngine;
using UnityEngine.InputSystem;

public class ImageText : MonoBehaviour
{
    [SerializeField] private Texture2D crosshair;
    [SerializeField] private string promptText = "Click on mouse";
    [SerializeField] private Vector2 labelSize = new Vector2(200, 50);

    private bool triggered = false;
    private Camera mainCamera;
    private Collider colliderObj;

    private void Awake()
    {
        mainCamera = Camera.main;
        colliderObj = GetComponent<Collider>();
        if (colliderObj == null)
        {
            Debug.LogError("Este objeto necesita un Collider para detectar el ratón.");
        }
    }

    private void Update()
    {
        // Solo si hay cámara y collider
        if (mainCamera == null || colliderObj == null) return;

        // Obtener posición del ratón desde el nuevo Input System
        Vector2 mousePosition = Mouse.current != null ? Mouse.current.position.ReadValue() : Vector2.zero;
        Ray ray = mainCamera.ScreenPointToRay(mousePosition);
        RaycastHit hit;

        // Comprobar si el rayo golpea este objeto
        if (Physics.Raycast(ray, out hit))
        {
            triggered = hit.collider == colliderObj;
        }
        else
        {
            triggered = false;
        }
    }

    private void OnGUI()
    {
        if (!triggered || crosshair == null) return;

        // Posición del texto centrado
        Vector2 labelPos = new Vector2(Screen.width / 2 - labelSize.x / 2, Screen.height / 2 + 50);
        GUI.Label(new Rect(labelPos.x, labelPos.y, labelSize.x, labelSize.y), promptText);

        // Dibujar mira centrada
        Rect crosshairRect = new Rect(
            (Screen.width - crosshair.width) / 2f,
            (Screen.height - crosshair.height) / 2f,
            crosshair.width,
            crosshair.height
        );
        GUI.DrawTexture(crosshairRect, crosshair);
    }
}
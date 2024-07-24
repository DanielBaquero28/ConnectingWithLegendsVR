using UnityEngine;
using UnityEngine.UI;

public class PromptUIController : MonoBehaviour
{
    public Button SubmitButton;
    public AIResponseController AiResponseController;


    void Start()
    {
        SubmitButton.onClick.AddListener(AiResponseController.OnSubmit);    
    }


}

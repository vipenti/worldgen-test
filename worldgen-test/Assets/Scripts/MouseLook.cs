using UnityEngine;
using UnityEngine.InputSystem; 

public class MouseLook : MonoBehaviour
{
    [Tooltip("Sensibilità del mouse. Regolala nell'Inspector (valori consigliati tra 0.05 e 0.5 ora che DeltaTime è rimosso).")]
    public float mouseSensitivity = 0.1f;
    public Transform playerBody;
    float xRotation = 0f;

    void Start()
    {
        Cursor.lockState = CursorLockMode.Locked;
    }

    void Update()
    {
        if (LLMWorldDirector.IsUIFocused) return;

        // Nel nuovo sistema leggiamo il delta di spostamento accumulato nel frame
        Vector2 mouseDelta = Mouse.current.delta.ReadValue();
        
        // CORREZIONE CRITICA: Rimosso il moltiplicatore * Time.deltaTime.
        // Moltiplicare per deltaTime causava vistosi scatti ed imprecisioni della visuale
        // non appena la pipeline procedurale attivava o disattivava i chunk attorno al cono visivo.
        float mouseX = mouseDelta.x * mouseSensitivity;
        float mouseY = mouseDelta.y * mouseSensitivity;

        xRotation -= mouseY;
        xRotation = Mathf.Clamp(xRotation, -90f, 90f);

        transform.localRotation = Quaternion.Euler(xRotation, 0f, 0f);
        playerBody.Rotate(Vector3.up * mouseX);
    }
}
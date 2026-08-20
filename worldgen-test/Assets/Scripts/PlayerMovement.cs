using UnityEngine;
using UnityEngine.InputSystem;

public class PlayerMovement : MonoBehaviour
{
    public CharacterController controller;

    public float speed = 12f;
    public float gravity = -19.62f; // Gravità leggermente più forte per un feeling migliore
    public float jumpHeight = 3f;   // Altezza del salto in metri

    [Header("Safety net anti-sprofondamento")]
    [Tooltip("Se la Y del player scende sotto questa soglia (fuori dal mondo per un bug di collider/generazione), viene riportato a fallRecoveryY.")]
    public float fallRecoveryThresholdY = -100f;
    public float fallRecoveryY = 50f;

    Vector3 velocity;
    bool isGrounded;

    void Update()
    {
        if (LLMWorldDirector.IsUIFocused) return;

        if (transform.position.y < fallRecoveryThresholdY)
        {
            RecoverFromFall();
            return;
        }

        // 1. Controllo se il player è a terra
        // Usiamo isGrounded del controller, ma resettiamo la velocità Y
        isGrounded = controller.isGrounded;

        if (isGrounded && velocity.y < 0)
        {
            // Mettiamo -2f invece di 0 per assicurarci che il player 
            // resti ben incollato al terreno
            velocity.y = -2f; 
        }

        // 2. Movimento WASD
        float x = 0;
        float z = 0;
        if (Keyboard.current.wKey.isPressed) z = 1;
        if (Keyboard.current.sKey.isPressed) z = -1;
        if (Keyboard.current.aKey.isPressed) x = -1;
        if (Keyboard.current.dKey.isPressed) x = 1;

        Vector3 move = transform.right * x + transform.forward * z;
        controller.Move(move * speed * Time.deltaTime);

        // 3. Salto
        // Se premi Spazio ed è a terra, applica la formula: v = sqrt(h * -2 * g)
        if (Keyboard.current.spaceKey.wasPressedThisFrame && isGrounded)
        {
            velocity.y = Mathf.Sqrt(jumpHeight * -2f * gravity);
        }

        // 4. Gravità
        velocity.y += gravity * Time.deltaTime;
        controller.Move(velocity * Time.deltaTime);
    }

    // Rete di sicurezza per i bug di collider/generazione dei chunk (es. sprofondamento durante
    // il cambio bioma): se il player finisce sotto il mondo, lo riportiamo sopra invece di
    // lasciarlo cadere all'infinito. Disabilitare temporaneamente il CharacterController prima di
    // spostare il transform evita che "combatta" lo spostamento manuale nello stesso frame.
    void RecoverFromFall()
    {
        controller.enabled = false;
        transform.position = new Vector3(transform.position.x, fallRecoveryY, transform.position.z);
        controller.enabled = true;
        velocity = Vector3.zero;
    }
}
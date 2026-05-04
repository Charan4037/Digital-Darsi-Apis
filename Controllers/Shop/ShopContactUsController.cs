using Microsoft.AspNetCore.Mvc;
using BagistoApi.Data;

namespace BagistoApi.Controllers.Shop;

[ApiController]
[Route("api/shop/contact-us")]
[Tags("ContactUs")]
public class ShopContactUsController : ControllerBase
{
    public record ContactUsRequest(string Name, string Email, string Subject, string Message);

    /// <summary>Submit contact form</summary>
    [HttpPost]
    public IActionResult SubmitContactForm([FromBody] ContactUsRequest req)
    {
        if (string.IsNullOrWhiteSpace(req.Name))
            return BadRequest(new { message = "Name is required." });

        if (string.IsNullOrWhiteSpace(req.Email))
            return BadRequest(new { message = "Email is required." });

        if (string.IsNullOrWhiteSpace(req.Subject))
            return BadRequest(new { message = "Subject is required." });

        if (string.IsNullOrWhiteSpace(req.Message))
            return BadRequest(new { message = "Message is required." });

        return Ok(new { message = "Your inquiry has been submitted successfully." });
    }
}

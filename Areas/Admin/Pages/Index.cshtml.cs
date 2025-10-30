using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc.RazorPages;

namespace NextStakeWebApp.Areas.Admin.Pages
{
    [Authorize(Roles = "SuperAdmin")]
    public class IndexModel : PageModel
    {
        public void OnGet() { }
    }
}

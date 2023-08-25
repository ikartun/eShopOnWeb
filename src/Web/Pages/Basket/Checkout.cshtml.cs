using System.Collections.Generic;
using System.Net;
using System.Net.Mime;
using System.Text;
using Ardalis.GuardClauses;
using Azure.Messaging.ServiceBus;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.Mvc.RazorPages;
using Microsoft.eShopWeb.ApplicationCore.Entities.OrderAggregate;
using Microsoft.eShopWeb.ApplicationCore.Exceptions;
using Microsoft.eShopWeb.ApplicationCore.Interfaces;
using Microsoft.eShopWeb.Infrastructure.Identity;
using Microsoft.eShopWeb.Web.Interfaces;
using Microsoft.Extensions.Options;
using Newtonsoft.Json.Linq;

namespace Microsoft.eShopWeb.Web.Pages.Basket;

[Authorize]
public class CheckoutModel : PageModel
{
    private readonly IBasketService _basketService;
    private readonly SignInManager<ApplicationUser> _signInManager;
    private readonly IOrderService _orderService;
    private string? _username = null;
    private readonly IBasketViewModelService _basketViewModelService;
    private readonly IAppLogger<CheckoutModel> _logger;
    private AppSettings AppSettings { get; set; }

    public CheckoutModel(IBasketService basketService,
        IBasketViewModelService basketViewModelService,
        SignInManager<ApplicationUser> signInManager,
        IOrderService orderService,
        IAppLogger<CheckoutModel> logger,
        IOptions<AppSettings> settings)
    {
        _basketService = basketService;
        _signInManager = signInManager;
        _orderService = orderService;
        _basketViewModelService = basketViewModelService;
        _logger = logger;
        AppSettings = settings.Value;
    }

    public BasketViewModel BasketModel { get; set; } = new BasketViewModel();

    public async Task OnGet()
    {
        await SetBasketModelAsync();
    }

    public async Task<IActionResult> OnPost(IEnumerable<BasketItemViewModel> items)
    {
        try
        {
            await SetBasketModelAsync();

            if (!ModelState.IsValid)
            {
                return BadRequest();
            }

            var updateModel = items.ToDictionary(b => b.Id.ToString(), b => b.Quantity);
            var address = new Address("123 Main St.", "Kent", "OH", "United States", "44240");
            await _basketService.SetQuantities(BasketModel.Id, updateModel);
            await _orderService.CreateOrderAsync(BasketModel.Id, address);
            await _basketService.DeleteBasketAsync(BasketModel.Id);

            string orderItems = prepareOrderItems(updateModel);
            string orderDetails = prepareOrderDetails(BasketModel, address.ToString());

            //callAzureFunc(orderItems, AppSettings.OrderItemsReserverFunctionURL);
            callAzureFunc(orderDetails, AppSettings.OrderDetailsProcessorFunctionURL);
            pushMessageToServiceBusAsync(orderItems, AppSettings.OrderDetailsProcessorServiceBusURL).GetAwaiter().GetResult();
        }
        catch (EmptyBasketOnCheckoutException emptyBasketOnCheckoutException)
        {
            //Redirect to Empty Basket page
            _logger.LogWarning(emptyBasketOnCheckoutException.Message);
            return RedirectToPage("/Basket/Index");
        }

        return RedirectToPage("Success");
    }

    private async Task pushMessageToServiceBusAsync(string orderDetails, string Url)
    {
        const string QueueName = "orderitemsreserver";

        await using var client = new ServiceBusClient(Url);

        await using ServiceBusSender sender = client.CreateSender(QueueName);
        try
        {
            var message = new ServiceBusMessage(orderDetails);
            _logger.LogWarning($"Sending message: {message}");
            await sender.SendMessageAsync(message);
        }
        catch (Exception exception)
        {
            Console.WriteLine($"{DateTime.Now} :: Exception: {exception.Message}");
        }
        finally
        {
            await sender.DisposeAsync();
            await client.DisposeAsync();
        }
    }

    private void callAzureFunc(string orderDetails, string Url)
    {
        var httpContent = new StringContent(orderDetails, Encoding.UTF8, "application/json");
        var client = new HttpClient();
        var result = client.PostAsync(Url, httpContent).GetAwaiter().GetResult();
    }

    private string prepareOrderDetails(BasketViewModel basketModel, string address)
    {
        List<int> orderedItems = new List<int>();

        foreach (var item in basketModel.Items)
        {
            orderedItems.Add(item.CatalogItemId);
        }
        string orderDetails = "{\"address\": \"" + address + "\", \"items\": [" + string.Join(",", orderedItems) + "], \"totalPrice\": " + basketModel.Total() + "}";
        _logger.LogInformation(orderDetails);
        return orderDetails;
    }

    private string prepareOrderItems(Dictionary<string, int> updateModel)
    {
        string orderItems = "{\"order_details\": [" + string.Join(", ", updateModel.Select(kv => "{\"id\":" + kv.Key + ", \"quantity\":" + kv.Value + "}").ToArray()) + "]}";
        _logger.LogInformation(orderItems);
        return orderItems;
    }

    private async Task SetBasketModelAsync()
    {
        Guard.Against.Null(User?.Identity?.Name, nameof(User.Identity.Name));
        if (_signInManager.IsSignedIn(HttpContext.User))
        {
            BasketModel = await _basketViewModelService.GetOrCreateBasketForUser(User.Identity.Name);
        }
        else
        {
            GetOrSetBasketCookieAndUserName();
            BasketModel = await _basketViewModelService.GetOrCreateBasketForUser(_username!);
        }
    }

    private void GetOrSetBasketCookieAndUserName()
    {
        if (Request.Cookies.ContainsKey(Constants.BASKET_COOKIENAME))
        {
            _username = Request.Cookies[Constants.BASKET_COOKIENAME];
        }
        if (_username != null) return;

        _username = Guid.NewGuid().ToString();
        var cookieOptions = new CookieOptions();
        cookieOptions.Expires = DateTime.Today.AddYears(10);
        Response.Cookies.Append(Constants.BASKET_COOKIENAME, _username, cookieOptions);
    }
}

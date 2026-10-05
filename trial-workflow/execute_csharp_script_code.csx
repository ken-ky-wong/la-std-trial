// Add the required libraries
#r "Newtonsoft.Json"
#r "Microsoft.Azure.Workflows.Scripting"
using System;
using System.Net.Http;
using System.Security.Authentication;
using System.Text;
using Microsoft.AspNetCore.Mvc;
using Microsoft.Extensions.Primitives;
using Microsoft.Extensions.Logging;
using Microsoft.Azure.Workflows.Scripting;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

/// <summary>
/// Executes the inline csharp code.
/// </summary>
/// <param name="context">The workflow context.</param>
/// <remarks> This is the entry-point to your code. The function signature should remain unchanged.</remarks>
public static async Task<Results> Run(WorkflowContext context, ILogger log)
{
  var triggerOutputs = (await context.GetTriggerResults().ConfigureAwait(false)).Outputs;
  var request = triggerOutputs["body"];
  var amount = request["amount"].Value<decimal>();
  var invoiceNumber = request["orderId"].Value<string>();

  var apiLoginId = Environment.GetEnvironmentVariable("AUTHORIZE_NET_API_LOGIN_ID");
  var transactionKey = Environment.GetEnvironmentVariable("AUTHORIZE_NET_TRANSACTION_KEY");
  var apiEndpoint = Environment.GetEnvironmentVariable("AUTHORIZE_NET_API_ENDPOINT");
  var receiptUrl = Environment.GetEnvironmentVariable("ACCEPT_HOSTED_RECEIPT_URL");
  var cancelUrl = Environment.GetEnvironmentVariable("ACCEPT_HOSTED_CANCEL_URL");

  if (string.IsNullOrWhiteSpace(apiLoginId) || string.IsNullOrWhiteSpace(transactionKey))
  {
    throw new InvalidOperationException("Authorize.Net API login ID and transaction key must be configured.");
  }

  if (!Uri.TryCreate(apiEndpoint, UriKind.Absolute, out var apiUri) || apiUri.Scheme != Uri.UriSchemeHttps)
  {
    throw new InvalidOperationException("Authorize.Net API endpoint must be an absolute HTTPS URL.");
  }

  if (!IsHttpUrl(receiptUrl) || !IsHttpUrl(cancelUrl))
  {
    throw new InvalidOperationException("Accept Hosted receipt and cancel URLs must be absolute HTTP or HTTPS URLs.");
  }

  var requestBody = new
  {
    getHostedPaymentPageRequest = new
    {
      merchantAuthentication = new
      {
        name = apiLoginId,
        transactionKey
      },
      transactionRequest = new
      {
        transactionType = "authCaptureTransaction",
        amount,
        order = new
        {
          invoiceNumber,
          description = "Product Description"
        }
      },
      hostedPaymentSettings = new
      {
        setting = new[]
        {
          Setting("hostedPaymentButtonOptions", new { text = "Pay" }),
          Setting("hostedPaymentReturnOptions", new
          {
            showReceipt = false,
            url = AddOrderQuery(receiptUrl, invoiceNumber),
            urlText = "Continue to receipt",
            cancelUrl = AddOrderQuery(cancelUrl, invoiceNumber),
            cancelUrlText = "Cancel"
          }),
          Setting("hostedPaymentOrderOptions", new { show = false }),
          Setting("hostedPaymentPaymentOptions", new { cardCodeRequired = true }),
          Setting("hostedPaymentBillingAddressOptions", new { show = true, required = true }),
          Setting("hostedPaymentShippingAddressOptions", new { show = false, required = false }),
          Setting("hostedPaymentSecurityOptions", new { captcha = false }),
          Setting("hostedPaymentStyleOptions", new { bgColor = "green" }),
          Setting("hostedPaymentCustomerOptions", new { showEmail = true, requiredEmail = true })
        }
      }
    }
  };

  var handler = new HttpClientHandler
  {
    SslProtocols = SslProtocols.Tls12
  };
  using (var httpClient = new HttpClient(handler))
  using (var content = new StringContent(JsonConvert.SerializeObject(requestBody), Encoding.UTF8, "application/json"))
  using (var response = await httpClient.PostAsync(apiUri, content).ConfigureAwait(false))
  {
    var responseBody = await response.Content.ReadAsStringAsync().ConfigureAwait(false);
    var responseRoot = JObject.Parse(responseBody);
    var apiResponse = responseRoot["getHostedPaymentPageResponse"] as JObject ?? responseRoot;
    var messages = apiResponse.GetValue("messages", StringComparison.OrdinalIgnoreCase) as JObject;
    var resultCode = messages?.GetValue("resultCode", StringComparison.OrdinalIgnoreCase)?.Value<string>();
    var token = apiResponse.GetValue("token", StringComparison.OrdinalIgnoreCase)?.Value<string>();

    if (!response.IsSuccessStatusCode ||
        !string.Equals(resultCode, "Ok", StringComparison.OrdinalIgnoreCase) ||
        string.IsNullOrWhiteSpace(token))
    {
      throw new InvalidOperationException("Authorize.Net did not create the hosted payment session.");
    }

    return new Results { Token = token };
  }
}

public static object Setting<T>(string name, T value) => new
{
  settingName = name,
  settingValue = JsonConvert.SerializeObject(value)
};

public static string AddOrderQuery(string url, string invoiceNumber)
{
  var uriBuilder = new UriBuilder(url);
  var query = uriBuilder.Query.TrimStart('?');
  var orderParameter = "order=" + Uri.EscapeDataString(invoiceNumber);
  uriBuilder.Query = string.IsNullOrEmpty(query) ? orderParameter : query + "&" + orderParameter;
  return uriBuilder.Uri.ToString();
}

public static bool IsHttpUrl(string url) =>
  Uri.TryCreate(url, UriKind.Absolute, out var uri) &&
  (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps);

public class Results
{
  public string Token {get; set;}
}
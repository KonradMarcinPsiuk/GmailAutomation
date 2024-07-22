using System.Net;
using Google;
using Google.Apis.Auth.OAuth2;
using Google.Apis.Gmail.v1;
using Google.Apis.Gmail.v1.Data;
using Google.Apis.Services;
using Google.Apis.Util.Store;

namespace GmailAutomation;

static class Program
{
    private static readonly string[] Scopes = [GmailService.Scope.GmailModify];
    private const string SecretFilePath = "C:/client_secret.json";

    private static readonly SemaphoreSlim RateLimiter = new(100, 100);
    private static readonly TimeSpan RateLimitInterval = TimeSpan.FromMinutes(1);
    
    private const int MaxRetries = 5;
    private static int _requestCount = 0;
    private static DateTime _resetTime = DateTime.UtcNow.Add(RateLimitInterval);

    static async Task Main()
    {
        UserCredential credential;

        await using (FileStream stream = new(SecretFilePath, FileMode.Open, FileAccess.Read))
        {
            const string credPath = "token";
            credential = await GoogleWebAuthorizationBroker.AuthorizeAsync(
                clientSecrets: (await GoogleClientSecrets.FromStreamAsync(stream)).Secrets,
                Scopes,
                "user",
                CancellationToken.None,
                new FileDataStore(credPath, true));
            Console.WriteLine("Credential file saved to: " + credPath);
        }

        GmailService service = new(new BaseClientService.Initializer()
        {
            HttpClientInitializer = credential,
        });

        List<string> allMessageIds = new();
        string? pageToken = null;

        UsersResource.MessagesResource.ListRequest request = service.Users.Messages.List("me");
        request.LabelIds = "INBOX";
        request.IncludeSpamTrash = false;
        request.Q = "is:unread";

        do
        {
            request.PageToken = pageToken;

            ListMessagesResponse response = await request.ExecuteAsync();

            if (response.Messages is not { Count: > 0 })
            {
                continue;
            }

            allMessageIds.AddRange(response.Messages.Select(messageItem => messageItem.Id));
            pageToken = response.NextPageToken;
        } while (pageToken != null);

        List<Task> tasks = allMessageIds.Select(messageId => RemoveUnreadLabel(service, "me", messageId)).ToList();

        await Task.WhenAll(tasks);
    }

    static async Task RemoveUnreadLabel(GmailService service, string userId, string messageId)
    {
        ModifyMessageRequest modifyRequest = new ModifyMessageRequest
        {
            RemoveLabelIds = new List<string> { "UNREAD" }
        };

        await ExecuteWithRetry(() => service.Users.Messages.Modify(modifyRequest, userId, messageId).ExecuteAsync());
        Console.WriteLine($"Message {messageId} marked as read.");
    }

    private static async Task ExecuteWithRetry<T>(Func<Task<T>> action)
    {
        int retries = 0;
        while (true)
        {
            await RateLimiter.WaitAsync();

            try
            {
                if (_requestCount >= 100 && DateTime.UtcNow < _resetTime)
                {
                    await Task.Delay(_resetTime - DateTime.UtcNow);
                }

                T result = await action();
                _requestCount++;
                return;
            }
            catch (GoogleApiException ex) when (ex.HttpStatusCode == HttpStatusCode.TooManyRequests)
            {
                retries++;
                if (retries >= MaxRetries)
                {
                    throw;
                }
                int delay = (int)Math.Pow(2, retries) * 1000;
                Console.WriteLine($"Rate limit exceeded. Waiting {delay} ms before retrying.");
                await Task.Delay(delay);
            }
            finally
            {
                RateLimiter.Release();

                if (DateTime.UtcNow >= _resetTime)
                {
                    _requestCount = 0;
                    _resetTime = DateTime.UtcNow.Add(RateLimitInterval);
                }
            }
        }
    }
}
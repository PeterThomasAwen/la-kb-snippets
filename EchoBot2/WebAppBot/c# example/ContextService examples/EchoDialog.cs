using System;
using System.Threading.Tasks;

using Microsoft.Bot.Connector;
using Microsoft.Bot.Builder.Dialogs;
using Microsoft.Bot.Builder.ConnectorEx;
using Newtonsoft.Json;
using System.Net.Http;
using System.Timers;
using System.Collections.Generic;
using Cafex.LiveAssist.Bot;

namespace Microsoft.Bot.Sample.SimpleEchoBot
{
    [Serializable]
    public class EchoDialog : IDialog<object>
    {
        protected int count = 1;
        private static Sdk sdk;
        private static Timer timer;

        // This simple sample handles one chatContext, i.e one concurrent chat.
        // A production solution would persist multiple chatContexts and transcripts.        
        private static ChatContext chatContext;
        private static List<TranscriptLine> transcript;
        private static string conversationRef;


        public async Task StartAsync(IDialogContext context)
        {
            sdk = sdk ?? new Sdk(new SdkConfiguration()
            {
                AccountNumber = "__ ACCOUNT NUMBER ___",  // Live assist account number.
                ContextDataHost = "__CONTEXT DATA HOST___" // Host name of the context data service.
            });

            transcript = new List<TranscriptLine>();
            context.Wait(MessageReceivedAsync);
        }

        public async Task MessageReceivedAsync(IDialogContext context, IAwaitable<IMessageActivity> argument)
        {
            var activity = await argument as Activity;


            if (chatContext != null) 
            {
              // As chatContext is not null we already have an escalated chat.
              // Post the incoming message line to the escalated chat
              await sdk.PostLine(activity.Text, chatContext);
            }
            else if (activity.Text == "reset")
            {
                PromptDialog.Confirm(
                    context,
                    AfterResetAsync,
                    "Are you sure you want to reset the count?",
                    "Didn't get that!",
                    promptStyle: PromptStyle.Auto);
            }
            else if (activity.Text.Contains("help"))
            {
                transcript.Add(new TranscriptLine()
                {
                    IsBot = false,
                    Timestamp = DateTime.Now,
                    SrcName = activity.From.Name,
                    Line = activity.Text
                });
                // "help" within the message is our escalation trigger.
                await context.PostAsync("Escalating to agent");
                await Escalate(activity); // Implemented in next step.
            }
            else
            {
                var message = $"{this.count++}: You said {activity.Text}";

                transcript.Add(new TranscriptLine()
                {
                    IsBot = false,
                    Timestamp = DateTime.Now,
                    SrcName = activity.From.Name,
                    Line = activity.Text
                });

                transcript.Add(new TranscriptLine()
                {
                    IsBot = true,
                    Timestamp = DateTime.Now,
                    SrcName = "EscalationBot",
                    Line = message
                });

                await context.PostAsync(message);
                context.Wait(MessageReceivedAsync);
            }
        }

        private async Task Escalate(Activity activity)
        {
            // This is our reference to the upstream conversation
            conversationRef = JsonConvert.SerializeObject(activity.ToConversationReference());

            var chatSpec = new ChatSpec()
            {
                // Set Agent skill to target
                Skill = "MySkill",
                Transcript = transcript,
                VisitorName = activity.From.Name,
                ContextData = CreateJwt
            };

            // Start timer to poll for Live Assist chat events
            if (timer == null)
            {
                timer = timer ?? new Timer(5000);
                // OnTimedEvent is implemented in the next step
                timer.Elapsed += (sender, e) => OnTimedEvent(sender, e);
                timer.Start();
            }

            // Request a chat via the Sdk    
            chatContext = await sdk.RequestChat(chatSpec);
        }

        public static String CreateJwt(string contextId)
        {
            var contextData = new ContextData()
            {
                customer = new Customer()
                {
                    firstName = new AssertedString()
                    {
                        value = "Sid"
                    },
                    lastName = new AssertedString()
                    {
                        value = "James",
                        isAsserted = true
                    },
                    companySize = new AssertedInteger()
                    {
                        value = 10
                    }
                }
            };

            var jwt = Jwt.Create(contextId, contextData);
            return jwt;
        }


        public async Task AfterResetAsync(IDialogContext context, IAwaitable<bool> argument)
        {
            var confirm = await argument;
            if (confirm)
            {
                this.count = 1;
                await context.PostAsync("Reset count.");
            }
            else
            {
                await context.PostAsync("Did not reset count.");
            }
            context.Wait(MessageReceivedAsync);
        }

        async void OnTimedEvent(Object source, ElapsedEventArgs eea)
        {
            if (chatContext != null)
            {
                // Create an upstream reply
                var reply = JsonConvert.DeserializeObject<ConversationReference>(conversationRef)
                    .GetPostToBotMessage().CreateReply();

                // Create upstream connection on which to send reply 
                var client = new ConnectorClient(new Uri(reply.ServiceUrl));

                // Poll Live Assist for events
                var chatInfo = await sdk.Poll(chatContext);

                if (chatInfo != null)
                {
                    // ChatInfo.ChatEvents will contain events since last call to poll.
                    if (chatInfo.ChatEvents != null && chatInfo.ChatEvents.Count > 0)
                    {
                        foreach (ChatEvent e in chatInfo.ChatEvents)
                        {
                            switch (e.Type)
                            {
                                // type is either "state" or "line".
                                case "line":
                                    // Source is either: "system", "agent" or "visitor"
                                    if (e.Source.Equals("system"))
                                    {
                                        reply.From.Name = "system";
                                    }
                                    else if (e.Source.Equals("agent"))
                                    {
                                        reply.From.Name = chatInfo.AgentName;

                                    }
                                    else
                                    {
                                        break;
                                    }

                                    reply.Type = "message";
                                    reply.Text = e.Text;
                                    client.Conversations.ReplyToActivity(reply);
                                    break;

                                case "state":
                                    // State changes
                                    // Valid values: "waiting", "chatting", "ended"
                                    if (chatInfo.State.Equals("ended"))
                                    {
                                        chatContext = null;
                                    }
                                    break;
                            }
                        }
                    }
                }
            }
        }

    }
}
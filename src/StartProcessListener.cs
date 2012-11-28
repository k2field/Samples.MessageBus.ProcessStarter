using SourceCode.Hosting.Client.BaseAPI;
using SourceCode.MessageBus;
using SourceCode.Workflow.Client;
using System;
using System.Collections.Generic;
using System.ComponentModel.Composition;
using System.ComponentModel.Composition.Hosting;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Mime;
using System.Text;

namespace SourceCode.Samples.MessageBus.ProcessStarter
{
    // Indicate to MessageBus that this class needs to be loaded
    // and allowed to process incoming messages.
    [MessageListenerExport(Priority = 50)]
    public class StartProcessListener : IMessageListener, IPartImportsSatisfiedNotification
    {
        // Uncomment this if you want to see inspect
        // it and see what imports and exports are
        // available.
        //[Import(typeof(CompositionContainer))]
        //private CompositionContainer _container;

        // Grab the primary message destination, destinations
        // are used to send messages.
        [PrimaryMessageDestinationImport]
        private IMessageDestination _destination;

        // Get the port.
        [Import("SourceCode.K2Server.Port", typeof(int))]
        private uint _k2ServerPort;
        private string _k2ServerConnectionString;

        public ContinuationAction MessageReceived(ListenerContext e)
        {
            // You can inherit from MessageExtendedInformation if you wish,
            // the likely scenario for this is if you support plugins yourself.
            var extended = e.ReceivedInformation.Message.GetExtendedInformation<MessageExtendedInformation>();
            var replyExtended = extended.Invert(null);

            string workflowName;
            if (extended.Message.Title.StartsWith("START: ", StringComparison.OrdinalIgnoreCase))
            {
                workflowName = extended.Message.Title.Substring(7).Trim();
            }
            else
            {
                return ContinuationAction.Continue; // Allow other plugins to execute.
            }

            // Get the message body.
            string body;
            try
            {
                using (var bodyStream = e.ReceivedInformation.Message.OpenView(new ContentType("text/plain")))
                {
                    if (bodyStream != null)
                    {
                        // Another plugin may have moved the position within the stream.
                        bodyStream.Seek(0, System.IO.SeekOrigin.Begin);
                        using (var sr = new StreamReader(bodyStream))
                        {
                            body = sr.ReadToEnd().Trim();
                        }
                    }
                    else
                    {
                        body = string.Empty;
                    }
                }
            }
            catch
            {
                // TODO: Logging etc.
                return ContinuationAction.Continue; // Allow other plugins to execute.
            }

            // TODO: Get data from the body somehow.
            // You can also look into attachments for e.g. InfoPath forms:
            // e.ReceivedInformation.Message.Attachments

            try
            {
                var con = new Connection();
                var cs = new ConnectionSetup();
                cs.ParseConnectionString(_k2ServerConnectionString);

                try
                {
                    Exception lastException = null;
                    con.Open(cs);

                    // AlternateIdentities are identities with the same email
                    // address, most likely due badly configured claims.
                    for (var i = 0; i < e.AlternateIdentities.Length; i++)
                    {
                        var alt = e.AlternateIdentities[i];
                        string fqn;

                        // Search for a FQN in the identity.
                        if (alt.TryGetValue("Fqn", out fqn))
                        {
                            try
                            {
                                con.RevertUser();
                                con.ImpersonateUser(fqn);

                                var pi = con.CreateProcessInstance(workflowName);
                                // TODO: Set data in the workflow.
                                con.StartProcessInstance(pi);

                                // Tell the user the workflow was started.
                                _destination.ReplyTo(e.ReceivedInformation.Message, new MessageBodyReader("text/plain", "Workflow started"), replyExtended);

                                e.ReceivedInformation.Commit(); // Indicate we were able to handle the message.
                                return ContinuationAction.Halt; // Stop other plugins from executing.
                            }
                            catch (Exception ex)
                            {
                                // TODO: Logging etc.
                                // This isn't nessecarily a complete failure,
                                // one of the other alternate identities may be
                                // able to action this.
                                lastException = ex;
                            }
                        }
                    }

                    string message;
                    if (lastException != null)
                    {
                        // Identities exist, but the user likely doesn't have rights.
                        message = lastException.ToString();
                    }
                    else
                    {
                        // No identities exist.
                        message = "Could not find a K2 user for your email address.";
                    }

                    message = "The workflow could not be started: " + message;

                    // Respond with the error.
                    _destination.ReplyTo(e.ReceivedInformation.Message, new MessageBodyReader("text/plain", message), replyExtended);

                    e.ReceivedInformation.Commit(); // Indicate we were able to handle the message.
                    return ContinuationAction.Halt; // Stop other plugins from executing.
                }
                finally
                {
                    if (con != null)
                    {
                        con.Close();
                        con.Dispose();
                    }
                }
            }
            catch
            {
                // TODO: Logging etc.
                return ContinuationAction.Continue; // Allow other plugins to execute.
            }
        }

        void IPartImportsSatisfiedNotification.OnImportsSatisfied()
        {
            // Called when all [Import]s have been resolved.
            var builder = new SCConnectionStringBuilder();
            builder.Host = Dns.GetHostName();
            builder.Integrated = true;
            builder.IsPrimaryLogin = true;
            builder.Port = _k2ServerPort;
            _k2ServerConnectionString = builder.ConnectionString;
        }
    }
}

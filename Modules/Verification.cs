﻿using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Botwinder.Entities;
using Discord;
using RedditSharp;

using guid = System.UInt64;

namespace Botwinder.Modules
{
	public class Verification : IModule
	{
		public bool UpdateInProgress{ get; set; } = false;

		protected static Verification Instance = null;

		public static Verification Get()
		{
			return Instance;
		}

		private class HashedValue
		{
			public guid UserId;
			public guid ServerId;

			public HashedValue(guid userId, guid serverId)
			{
				this.UserId = userId;
				this.ServerId = serverId;
			}
		}

		public const string VerifySent = "<@{0}>, check your messages!";
		public const string VerifyDonePM = "You have been verified on the `{0}` server =]";
		public const string VerifyDone = "<@{0}> has been verified.";

		public const string VerifyError =
			"If you want me to send a PM with the info to someone, you have to @mention them (only one person though)";

		public const string UserNotFound = "I couldn't find them :(";

		private RedditSharp.Reddit RedditClient = null;
		private string LastVerifyMessage = "";
		private readonly List<string> RecentRedditMessages = new List<string>();

		private readonly ConcurrentDictionary<string, HashedValue> HashedValues = new ConcurrentDictionary<string, HashedValue>();


		public List<Command> Init<TUser>(IBotwinderClient<TUser> client) where TUser : UserData, new()
		{
			List<Command> commands;
			Command newCommand;

			if( !client.GlobalConfig.RedditEnabled )
			{
				commands = new List<Command>();
				newCommand = new Command("verify");
				newCommand.Type = Command.CommandType.ChatOnly;
				newCommand.Description = "Participate in currently active giveaway.";
				newCommand.OnExecute += async (sender, e) =>
				{
					await e.Message.Channel.SendMessageSafe(
						"Reddit verification is currently disabled for technical difficulties. Please be patient, we are working on it.");
				};
				commands.Add(newCommand);
				return commands;
			}

			commands = new List<Command>();

			try
			{
				Console.WriteLine("Reddit: Connecting...");
				//BotWebAgent agent = new BotWebAgent(client.GlobalConfig.RedditUsername, client.GlobalConfig.RedditPassword, client.GlobalConfig.RedditClientId, client.GlobalConfig.RedditClientSecret, client.GlobalConfig.RedditRedirectUri);
				//this.RedditClient = new RedditSharp.Reddit(agent, true);
				this.RedditClient = new RedditSharp.Reddit(client.GlobalConfig.RedditUsername, client.GlobalConfig.RedditPassword);
				Console.WriteLine("Reddit: Connected.");
			}
			catch( Exception e )
			{
				if( HandleException != null )
					HandleException(this, new ModuleExceptionArgs(e, "Module.Reddit.Init failed to connect RedditClient"));
				else
					throw;
			}

			Instance = this;

// !verify
			newCommand = new Command("verify");
			newCommand.Type = Command.CommandType.ChatOnly;
			newCommand.Description =
				"This will send you some info about verification. You can use this with a parameter to send the info to your friend - you have to @mention them.";
			newCommand.OnExecute += async (sender, e) =>
			{
				if( (!e.Server.ServerConfig.VerifyEnabled || e.Server.ServerConfig.VerifyRoleID == 0) &&
				    !client.IsGlobalAdmin(e.Message.User) )
				{
					await e.Message.Channel.SendMessageSafe("Verification is disabled on this server.");
					return;
				}

				Discord.User user = null;
				Server<UserData> server = e.Server as Server<UserData>;

				// GlobalAdmin verified someone somewhere.
				if( e.MessageArgs != null && e.MessageArgs.Length == 3 && server.IsGlobalAdmin(e.Message.User) )
				{
					if( e.Message.RawText == this.LastVerifyMessage )
						return;

					this.LastVerifyMessage = e.Message.RawText;

					guid serverID, userID;
					Discord.Server discordServer = null;
					if( !guid.TryParse(e.MessageArgs[0], out serverID) || (discordServer = client.GetServer(serverID)) == null ||
					    !guid.TryParse(e.MessageArgs[1], out userID) || (user = discordServer.GetUser(userID)) == null )
					{
						await e.Message.Channel.SendMessageSafe(UserNotFound);
						return;
					}

					await VerifyUser(user, client.GetServerData(serverID), (e.MessageArgs[2] == "force" ? null : e.MessageArgs[2]));
					await e.Message.Channel.SendMessageSafe(string.Format(VerifyDone, user.Id));
					return;
				}

				// Admin verified someone.
				if( e.MessageArgs != null && e.MessageArgs.Length == 2 && server.IsAdmin(e.Message.User) )
				{
					if( e.Message.RawText == this.LastVerifyMessage )
						return;

					this.LastVerifyMessage = e.Message.RawText;

					guid id;
					if( (e.Message.MentionedUsers.Count() != 1 || (user = e.Message.MentionedUsers.ElementAt(0)) == null) &&
					    (!guid.TryParse(e.MessageArgs[0], out id) || (user = e.Message.Server.GetUser(id)) == null) )
					{
						await e.Message.Channel.SendMessageSafe(UserNotFound);
						return;
					}

					await VerifyUser(user, server, (e.MessageArgs[1] == "force" ? null : e.MessageArgs[1]));
					await e.Message.Channel.SendMessageSafe(string.Format(VerifyDone, user.Id));
					return;
				}

				// Verify the author.
				if( string.IsNullOrEmpty(e.TrimmedMessage) )
				{
					user = e.Message.User;
				}

				// Verify mentioned user.
				if( e.Message.MentionedUsers != null && e.Message.MentionedUsers.Count() == 1 )
				{
					user = e.Message.MentionedUsers.ElementAt(0);
				}

				if( user == null )
				{
					await e.Message.Channel.SendMessageSafe(VerifyError);
				}
				else
				{
					if( await VerifyUserPM(user, server) )
						await e.Message.Channel.SendMessageSafe(string.Format(VerifyDone, user.Id));
					else
						await e.Message.Channel.SendMessageSafe(string.Format(VerifySent, user.Id));
				}
			};
			commands.Add(newCommand);

// !reddit
			newCommand = new Command("reddit");
			newCommand.Type = Command.CommandType.PmAndChat;
			newCommand.Description = "Print information about current reddit user.";
			newCommand.RequiredPermissions = Command.PermissionType.OwnerOnly;
			newCommand.OnExecute += async (sender, e) =>
			{
				string message = "";
				if( this.RedditClient == null )
					message = "_Reddit == null";
				else if( this.RedditClient.User == null )
					message = "_Reddit.User == null";
				else
				{
					message = string.Format("Mail: {0}\nModMail: {1}\nMessages: {2}", this.RedditClient.User.HasMail,
						(this.RedditClient.User.HasModMail ? this.RedditClient.User.ModMail.Count() : 0),
						this.RedditClient.User.UnreadMessages.Count());
				}

				await e.Message.Channel.SendMessageSafe(message);
			};
			commands.Add(newCommand);

// !redditReadAll
			newCommand = new Command("redditReadAll");
			newCommand.Type = Command.CommandType.PmAndChat;
			newCommand.Description = "Read all messages.";
			newCommand.RequiredPermissions = Command.PermissionType.OwnerOnly;
			newCommand.OnExecute += async (sender, e) =>
			{
				string message = "";
				if( this.RedditClient == null )
					message = "_Reddit == null";
				else if( this.RedditClient.User == null )
					message = "_Reddit.User == null";
				else
				{
					foreach( RedditSharp.Things.Thing thing in this.RedditClient.User.UnreadMessages )
					{
						try
						{
							RedditSharp.Things.PrivateMessage redditMessage = thing as RedditSharp.Things.PrivateMessage;
							if( redditMessage == null )
								message += "\nnull message: " + thing.Kind + " | " + thing.FullName;
							else
							{
								redditMessage.SetAsRead();
								message += "\nread: " + redditMessage.Body;
							}
						}
						catch( Exception ex )
						{
							message += "\nexception: " + ex.Message;
						}
					}
				}

				await e.Message.Channel.SendMessageSafe(message);
			};
			commands.Add(newCommand);

// !verifyAgain
			newCommand = new Command("verifyAgain");
			newCommand.Type = Command.CommandType.PmAndChat;
			newCommand.Description = "Verify the last `n` reddit messages.";
			newCommand.RequiredPermissions = Command.PermissionType.OwnerOnly;
			newCommand.OnExecute += async (sender, e) =>
			{
				int n = 0;
				string message = "";
				if( this.RedditClient == null )
					message = "_Reddit == null";
				else if( this.RedditClient.User == null )
					message = "_Reddit.User == null";
				else if( !int.TryParse(e.TrimmedMessage, out n) )
					message = "Invalid parameter.";
				else
				{

					await ReVerify(client, n);
					message = "Done.";
				}

				await e.Message.Channel.SendMessageSafe(message);
			};
			commands.Add(newCommand);

			return commands;
		}


		public async Task Update<TUser>(IBotwinderClient<TUser> client) where TUser : UserData, new()
		{
			if( !client.GlobalConfig.RedditEnabled || this.UpdateInProgress )
				return;

			this.UpdateInProgress = true;

			try
			{
				if( this.RedditClient == null || this.RedditClient.User == null || this.RedditClient.User.UnreadMessages == null )
				{
					this.UpdateInProgress = false;
					return;
				}

				Discord.Server mainServer = client.GetServer(client.GlobalConfig.MainServerID);
				Discord.Channel mainChannel = null;
				if( mainServer != null )
					mainChannel = mainServer.GetChannel(client.GlobalConfig.MainLogChannelID);

				int i = 0;
				RedditSharp.Things.PrivateMessage message = null;
				List<RedditSharp.Things.Thing> list = this.RedditClient.User.UnreadMessages.ToList();
				foreach( RedditSharp.Things.Thing thing in list )
				{
					if( ++i > 99 )
						break;

					try
					{
						message = thing as RedditSharp.Things.PrivateMessage;
						if( message == null )
							continue;

						//await message.SetAsReadAsync();
						message.SetAsRead();

						string link = message.Author.StartsWith("http") ? message.Author : message.Author.StartsWith("/u/") ?
							"https://www.reddit.com/user/" + message.Author.Remove(0, 3) :
							"https://www.reddit.com/user/" + message.Author;
						if( message.Author == null || message.Subject == null || message.Body == null ||
						    link == null ) //Make sure that reddit's things are not retarded, the naming is retarded enough already.
						{
							if( HandleException != null )
								HandleException(this,
									new ModuleExceptionArgs(null, string.Format("Module.Reddit.Update unexpectedly failed...\n  Author: `{0}`\n  Subject: `{1}`\n  Body: `{2}`",
											string.IsNullOrWhiteSpace(message.Author) ? "null" : message.Author,
											string.IsNullOrWhiteSpace(message.Subject) ? "null" : message.Subject,
											string.IsNullOrWhiteSpace(message.Body) ? "null" : message.Body)));
							continue;
						}

						if( this.RecentRedditMessages.Contains(message.Body) )
							continue;
						this.RecentRedditMessages.Add(message.Body);

						if( !string.IsNullOrWhiteSpace(message.Subject) && !string.IsNullOrWhiteSpace(message.Body)
						    && !message.IsComment && message.Subject.Contains("DiscordVerification") )
						{
							string[] ids = Regex.Split(Regex.Match(message.Body, "\\d+[\\s%]\\d+").Value, "\\s|%20");
							guid serverID = 0;
							guid userID = 0;
							Server<TUser> server = null;
							Discord.Server discordServer = null;
							Discord.User user = null;

							if( ids != null && ids.Length == 2 && guid.TryParse(ids[0], out serverID) && guid.TryParse(ids[1], out userID) &&
							    (discordServer = client.GetServer(serverID)) != null && client.Servers.ContainsKey(serverID) &&
							    (server = client.GetServerData(serverID)) != null && (user = discordServer.GetUser(userID)) != null )
							{
								await VerifyUser(user, server, link);
							}
							else
							{
								//await message.ReplyAsync("Hi!\n I'm sorry but something went wrong with the reddit message (or discord servers) and I couldn't verify you... I did however notify Rhea (my mum!) and she will take care of it!\nI would like to ask you for patience, she may not be online =]");
								//message.Reply("Hi!\n I'm sorry but something went wrong with the reddit message (or discord servers) and I couldn't verify you... I did however notify Rhea (my mum!) and she will take care of it!\nI would like to ask you for patience, she may not be online =]");
								if( mainChannel != null )
									await mainChannel.SendMessageSafe(string.Format("Invalid DiscordVerification message received.\n    Author: {0}\n    Subject: {1}\n    Message: {2}\n    Found User: <@{3}>\n    Link to Author: {4}",
										string.IsNullOrWhiteSpace(message.Author) ? "" : message.Author,
										string.IsNullOrWhiteSpace(message.Subject) ? "" : message.Subject,
										string.IsNullOrWhiteSpace(message.Body) ? "" : message.Body, user == null ? "null" : user.Id.ToString(),
										string.IsNullOrWhiteSpace(link) ? "" : link));
							}
						}
						else
						{
							if( mainChannel != null )
								await mainChannel.SendMessageSafe(string.Format("I received an unknown private message on reddit:\n{0}: {1}\n{2}",
									string.IsNullOrWhiteSpace(message.Author) ? "" : message.Author,
									string.IsNullOrWhiteSpace(message.Subject) ? "" : message.Subject,
									string.IsNullOrWhiteSpace(message.Body) ? "" : message.Body));
						}
					}
					catch( Exception e )
					{
						if( HandleException != null )
							HandleException(this, new ModuleExceptionArgs(e, "Module.Reddit.Update failed for " + thing.Kind + ": " + thing.Shortlink + " | " + thing.FullName));
						else
							throw;

						if( message != null )
							message.SetAsRead(); //The message is invalid, skip it.
					}
				}
			}
			catch( System.Net.WebException ){}
			catch( Exception e )
			{
				if( HandleException != null )
					HandleException(this, new ModuleExceptionArgs(e, "Module.Reddit.Update failed"));
				else
					throw;
			}

			this.UpdateInProgress = false;
		}

		//Returns true if the user is already verified.
		public async Task<bool> VerifyUserPM<TUser>(Discord.User user, Server<TUser> server) where TUser : UserData, new()
		{
			TUser userData = null;
			if( server.UserDatabase.TryGetValue(user.Id, out userData) && userData.Verified )
			{
				await VerifyUser(user, server);
				return true;
			}

			string verifyPm = server.ServerConfig.VerifyPM;
			if( !server.ServerConfig.VerifyUseReddit )
			{
				int source = Math.Abs((user.Id.ToString() + server.ID).GetHashCode());
				int chunkNum = (int)Math.Ceiling(Math.Ceiling(Math.Log(source)) / 2);
				StringBuilder hashBuilder = new StringBuilder(chunkNum);
				for( int i = 0; i < chunkNum; i++ )
				{
					char c = (char)((source % 100) / 4 + 97);
					hashBuilder.Append(c);
					source = source / 100;
				}
				string hash = hashBuilder.ToString();
				hash = hash.Length > 5 ? hash.Substring(0, 5) : hash;
				if( !this.HashedValues.ContainsKey(hash) )
					this.HashedValues.Add(hash, new HashedValue(user.Id, server.ID));

				verifyPm = "In order to get verified, you must reply to me with a hidden code within the below rules. " +
				           "Just the code by itself, do not add anything extra. Read the rules and you will find the code.\n" +
				           "_(Beware that this will expire in a few hours, " +
				           "if it does simply run the `verify` command in the server chat, " +
				           "and re-send the code that you already found - it won't change.)_";

				string[] lines = server.ServerConfig.VerifyPM.Split('\n');
				string[] words = null;
				bool found = false;
				for( int i = Utils.Random.Next(lines.Length / 2, lines.Length); i >= lines.Length / 2; i-- )
				{
					if( i <= lines.Length / 2 )
					{
						i = lines.Length - 1;
						continue;
					}
					if( (words = lines[i].Split(' ')).Length > 10 )
					{
						int space = Utils.Random.Next(words.Length / 4, words.Length - 1);
						lines[i] = lines[i].Insert(lines[i].IndexOf(words[space]) - 1, " the secret is: " + hash );

						hashBuilder = new StringBuilder(lines.Length+1);
						hashBuilder.AppendLine(verifyPm).AppendLine("");
						for(int j = 0; j < lines.Length; j++)
							hashBuilder.AppendLine(lines[j]);
						verifyPm = hashBuilder.ToString();
						found = true;
						break;
					}
				}

				if( !found )
				{
					verifyPm = "```diff\n- Error!\n\n  " +
							   "Please get in touch with the server administrator and let them know, that their Verification PM is invalid " +
							   "(It may be either too short, or too long. The algorithm is looking for lines with at least 10 words.)```";
				}
			}

			string message = string.Format("Hi {0},\nthe `{1}` server is using "+ (server.ServerConfig.VerifyUseReddit ? "reddit" : "code") +" verification.\n\n{2}\n\n",
				user.Name, user.Server.Name, verifyPm);

			if( server.ServerConfig.VerifyKarma > 0 )
				message += string.Format("You will also get {0} `{1}{2}` for verifying!\n\n", server.ServerConfig.VerifyKarma,
					server.ServerConfig.CommandCharacter, server.ServerConfig.KarmaCurrency);

			if( server.ServerConfig.VerifyUseReddit )
			{
				message += string.Format(
					"The only requirement is to have registered Discord account with valid email address, otherwise you may lose this status.\n" +
					"In order to complete the verification, please send me this message on Reddit _(Do not change anything, just click the link and hit send.)_\n" +
					"https://www.reddit.com/message/compose/?to=Botwinder&subject=DiscordVerification&message={0}%20{1}" +
					"\n_(Please note that this will **not** work on **mobile** version of Reddit." +
					"If you are on mobile, you have to 1. click the link, 2. click reddit menu, 3. click \"Desktop Site\", 4. hit \"send.\")_" +
					"\n\nCheers! :smiley:",
					user.Server.Id, user.Id);
			}

			await user.SendMessageSafe(message);
			return false;
		}

		/// <summary>Verifies a user based on a hashCode string and returns true if successful.</summary>
		public async Task<bool> VerifyUserHash<TUser>(IBotwinderClient<TUser> client, Discord.User user, string hashCode)
			where TUser : UserData, new()
		{
			if( !this.HashedValues.ContainsKey(hashCode) || this.HashedValues[hashCode].UserId != user.Id )
				return false;

			Server<TUser> server = client.GetServerData(this.HashedValues[hashCode].ServerId);
			await VerifyUser(server.DiscordServer.GetUser(user.Id), server, null);
			return true;
		}

		private async Task VerifyUser<TUser>(Discord.User user, Server<TUser> server, string verifiedInfo = null)
			where TUser : UserData, new()
		{
			if( user.Roles.FirstOrDefault(r => r.Id == server.ServerConfig.VerifyRoleID) != null )
				return;

			TUser userData = server.UserDatabase.GetOrAddUser(user);
			bool alreadyVerified = userData.Verified;
			if( !alreadyVerified && server.ServerConfig.VerifyKarma < int.MaxValue / 2f )
				userData.KarmaCount += server.ServerConfig.VerifyKarma;
			if( !string.IsNullOrEmpty(verifiedInfo) )
				userData.VerifiedInfo = verifiedInfo;

			userData.Verified = true;
			server.UserDatabase.SaveAsync();

			try
			{
				Discord.Role verifiedRole = server.DiscordServer.GetRole(server.ServerConfig.VerifyRoleID);
				if( verifiedRole != null )
					await user.AddRoles(verifiedRole);
			}
			catch( Exception exception )
			{
				if( HandleException != null )
					HandleException(this,
						new ModuleExceptionArgs(exception, server.Name + ": Failed to assign VerifyRole to " + user.Name));
				else
					throw;
			}

			try
			{
				if( !alreadyVerified )
					await user.SendMessageSafe(string.Format(VerifyDonePM, server.DiscordServer.Name));
			}
			catch( Exception ){}
		}

		public async Task ReVerify<TUser>(IBotwinderClient<TUser> client, int n) where TUser : UserData, new()
		{
			if( this.RedditClient == null || this.RedditClient.User == null || this.RedditClient.User.UnreadMessages == null )
				return;

			RedditSharp.Things.PrivateMessage message = null;
			foreach( RedditSharp.Things.Thing thing in this.RedditClient.User.UnreadMessages )
			{
				try
				{
					message = thing as RedditSharp.Things.PrivateMessage;
					if( message == null )
						continue;

					//await message.SetAsReadAsync();
					message.SetAsRead();

					string link = message.Author.StartsWith("http") ? message.Author : message.Author.StartsWith("/u/") ?
						"https://www.reddit.com/user/" + message.Author.Remove(0, 3) :
						"https://www.reddit.com/user/" + message.Author;
					if( message.Author == null || message.Subject == null || message.Body == null ||
						link == null ) //Make sure that reddit's things are not retarded, the naming is retarded enough already.
					{
						continue;
					}

					if( this.RecentRedditMessages.Contains(message.Body) )
						continue;
					this.RecentRedditMessages.Add(message.Body);

					if( !string.IsNullOrWhiteSpace(message.Subject) && !string.IsNullOrWhiteSpace(message.Body)
						&& !message.IsComment && message.Subject.Contains("DiscordVerification") )
					{
						string[] ids = Regex.Split(Regex.Match(message.Body, "\\d+[\\s%]\\d+").Value, "\\s|%20");
						guid serverID = 0;
						guid userID = 0;
						Server<TUser> server = null;
						Discord.Server discordServer = null;
						Discord.User user = null;

						if( ids != null && ids.Length == 2 && guid.TryParse(ids[0], out serverID) && guid.TryParse(ids[1], out userID) &&
							(discordServer = client.GetServer(serverID)) != null && client.Servers.ContainsKey(serverID) &&
							(server = client.GetServerData(serverID)) != null && (user = discordServer.GetUser(userID)) != null )
						{
							await VerifyUser(user, server, link);
						}
					}
				}
				catch( Exception )
				{
					if( message != null )
						message.SetAsRead(); //The message is invalid, skip it.
				}
				if( --n <= 0 )
					return;
			}
		}

		public event EventHandler<ModuleExceptionArgs> HandleException;
	}
}


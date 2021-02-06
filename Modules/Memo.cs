﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Valkyrja.core;
using Valkyrja.entities;
using Discord;
using Discord.WebSocket;
using guid = System.UInt64;

namespace Valkyrja.modules
{
	public class Memo: IModule
	{
		private const string MemoString = "Your {0} is:\n\n{1}";
		private const string MemoOtherString = "{0}'s {1} is:\n\n{2}";
		private const string NoMemoString = "You don't have a {0}.";
		private const string NoMemoOtherString = "{0} doesn't have a {1}.";
		private const string SetString = "All set:\n\n{0}";
		private const string ClearedString = "I've cleared your memo ~ rip. _\\*The bot presses F to pay respects.*_\n**F**";
		private const string NotFoundString = "User not found by that expression.";

		private ValkyrjaClient Client;

		private readonly Regex ProfileParamRegex = new Regex("--?\\w+\\s(?!--?\\w|$).*?(?=\\s--?\\w|$)", RegexOptions.Compiled | RegexOptions.Singleline, TimeSpan.FromMilliseconds(100));
		private readonly Regex ProfileOptionRegex = new Regex("--?\\w+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
		private readonly Regex ProfileEmptyOptionRegex = new Regex("--?\\w+(?=\\s--?\\w|$)", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));
		private readonly Regex UserIdRegex = new Regex("\\d+", RegexOptions.Compiled, TimeSpan.FromMilliseconds(100));


		public Func<Exception, string, guid, Task> HandleException{ get; set; }
		public bool DoUpdate{ get; set; } = false;

		public List<Command> Init(IValkyrjaClient iClient)
		{
			this.Client = iClient as ValkyrjaClient;
			List<Command> commands = new List<Command>();

// !memo
			Command newCommand = new Command("memo");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Display your (or other) memo.";
			newCommand.ManPage = new ManPage("[@user]", "`[@user]` - Optional username or mention to display memo of someone else.");
			newCommand.RequiredPermissions = PermissionType.Everyone;
			newCommand.OnExecute += async e => {
				if( !e.Server.Config.MemoEnabled )
				{
					await e.SendReplySafe("Memo is disabled on this server.");
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
				string response = "";

				if( !string.IsNullOrEmpty(e.TrimmedMessage) )
				{
					string expression = e.TrimmedMessage.ToLower();
					SocketUser user = e.Message.MentionedUsers.FirstOrDefault();
					if( user == null )
						user = e.Server.Guild.Users.FirstOrDefault(u => (u?.Username != null && u.Username.ToLower() == expression) || (u?.Nickname != null && u.Nickname.ToLower() == expression));

					if( user == null )
					{
						response = NotFoundString;
					}
					else
					{
						UserData userData = dbContext.GetOrAddUser(e.Server.Id, user.Id);
						if( string.IsNullOrEmpty(userData.Memo) )
							response = string.Format(NoMemoOtherString, user.GetNickname(), e.CommandId);
						else
							response = string.Format(MemoOtherString, user.GetNickname(), e.CommandId, userData.Memo);
					}
				}
				else
				{
					UserData userData = dbContext.GetOrAddUser(e.Server.Id, e.Message.Author.Id);
					if( string.IsNullOrEmpty(userData.Memo) )
						response = string.Format(NoMemoString, e.CommandId);
					else
						response = string.Format(MemoString, e.CommandId, userData.Memo);
				}

				dbContext.Dispose();
				await e.SendReplySafe(response);
			};
			commands.Add(newCommand);

// !setMemo
			newCommand = new Command("setMemo");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Set your memo.";
			newCommand.ManPage = new ManPage("<memo text>", "`<memo text>` - Text to be recorded as your memo.");
			newCommand.RequiredPermissions = PermissionType.Everyone;
			newCommand.OnExecute += async e => {
				if( !e.Server.Config.MemoEnabled )
				{
					await e.SendReplySafe("Memo is disabled on this server.");
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
				string response = "";

				UserData userData = dbContext.GetOrAddUser(e.Server.Id, e.Message.Author.Id);
				if( string.IsNullOrEmpty(e.TrimmedMessage) )
				{
					userData.Memo = "";
					response = ClearedString;
				}
				else
				{
					userData.Memo = e.TrimmedMessage;
					response = string.Format(SetString, userData.Memo);
				}

				dbContext.SaveChanges();
				dbContext.Dispose();
				await e.SendReplySafe(response);
			};
			commands.Add(newCommand);

// !profile
			newCommand = new Command("profile");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Display your (or other) profile. Get Help: setProfile --help";
			newCommand.ManPage = new ManPage("[@user]", "`[@user]` - Optional username or mention to display profile of someone else.");
			newCommand.RequiredPermissions = PermissionType.Everyone;
			newCommand.OnExecute += async e => {
				if( !this.Client.IsPremium(e.Server) && !this.Client.IsTrialServer(e.Server.Id) )
				{
					await e.SendReplySafe("User profiles are a subscriber-only feature.");
					return;
				}

				if( !e.Server.Config.ProfileEnabled )
				{
					await e.SendReplySafe("User profiles are disabled on this server.");
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);

				if( !string.IsNullOrEmpty(e.TrimmedMessage) )
				{
					string expression = e.TrimmedMessage.ToLower();
					SocketUser user = e.Message.MentionedUsers.FirstOrDefault();
					if( user == null )
						user = e.Server.Guild.Users.FirstOrDefault(u => (u?.Username != null && u.Username == e.TrimmedMessage) || (u?.Nickname != null && u.Nickname == e.TrimmedMessage));
					if( user == null )
						user = e.Server.Guild.Users.FirstOrDefault(u => (u?.Username != null && u.Username.ToLower() == expression) || (u?.Nickname != null && u.Nickname.ToLower() == expression));
					if( user == null )
						user = e.Server.Guild.Users.FirstOrDefault(u => (u?.Username != null && u.Username.ToLower().Contains(expression)) || (u?.Nickname != null && u.Nickname.ToLower().Contains(expression)));

					if( user == null )
					{
						await e.SendReplySafe(NotFoundString);
					}
					else if( user.Id == this.Client.DiscordClient.CurrentUser.Id )
					{
						await e.SendReplySafe(null, embed: GetValkyrjaEmbed(user as SocketGuildUser));
					}
					else
					{
						await e.SendReplySafe(null, embed: GetProfileEmbed(dbContext, e.Server, user as SocketGuildUser));
					}
				}
				else
				{
					await e.SendReplySafe(null, embed: GetProfileEmbed(dbContext, e.Server, e.Message.Author as SocketGuildUser));
				}

				dbContext.Dispose();
			};
			commands.Add(newCommand);

// !sendProfile
			newCommand = new Command("sendProfile");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Send your profile to preconfigured introduction channel. Get Help: setProfile --help";
			newCommand.ManPage = new ManPage("", "");
			newCommand.RequiredPermissions = PermissionType.Everyone;
			newCommand.OnExecute += async e => {
				if( !this.Client.IsPremium(e.Server) && !this.Client.IsTrialServer(e.Server.Id) )
				{
					await e.SendReplySafe("User profiles are a subscriber-only feature.");
					return;
				}

				IMessageChannel channel = null;
				if( !e.Server.Config.ProfileEnabled || e.Server.Config.ProfileChannelId == 0 || (channel = e.Server.Guild.GetTextChannel(e.Server.Config.ProfileChannelId)) == null )
				{
					await e.SendReplySafe("User profiles do not have a channel configured.");
					return;
				}

				guid lastMessageId = 0;
				while(true)
				{
					IEnumerable<IMessage> batch = await channel.GetMessagesAsync(lastMessageId+1, Direction.After, 100, CacheMode.AllowDownload).FlattenAsync();
					if( batch == null || !batch.Any() )
						break;
					lastMessageId = batch.Last().Id;

					IMessage message = batch.FirstOrDefault(m => m.Content != null && guid.TryParse(this.UserIdRegex.Match(m.Content).Value, out guid id) && id == e.Message.Author.Id);
					if( message != null )
					{
						await message.DeleteAsync();
						break;
					}
				}

				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);

				await channel.SendMessageAsync($"<@{e.Message.Author.Id}>'s introduction:", embed: GetProfileEmbed(dbContext, e.Server, e.Message.Author as SocketGuildUser));
				await e.SendReplySafe("It haz been done.");

				dbContext.Dispose();
			};
			commands.Add(newCommand);

// !getProfile
			newCommand = new Command("getProfile");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Get the source used to set your profile.";
			newCommand.ManPage = new ManPage("", "");
			newCommand.RequiredPermissions = PermissionType.Everyone;
			newCommand.OnExecute += async e => {
				if( !this.Client.IsPremium(e.Server) && !this.Client.IsTrialServer(e.Server.Id) )
				{
					await e.SendReplySafe("User profiles are a subscriber-only feature.");
					return;
				}

				if( !e.Server.Config.ProfileEnabled )
				{
					await e.SendReplySafe("User profiles are disabled on this server.");
					return;
				}

				StringBuilder response = new StringBuilder();
				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
				IEnumerable<ProfileOption> options = dbContext.ProfileOptions.AsQueryable().Where(o => o.ServerId == e.Server.Id).AsEnumerable().OrderBy(o => o.Order);
				foreach( ProfileOption option in options )
				{
					UserProfileOption userOption = dbContext.UserProfileOptions.AsQueryable().FirstOrDefault(o => o.ServerId == e.Server.Id && o.UserId == e.Message.Author.Id && o.Option == option.Option);
					if( userOption == null || string.IsNullOrWhiteSpace(userOption.Value) )
						continue;

					response.Append($"{userOption.Option} {userOption.Value} ");
				}
				response.Append("\n```");

				string responseString = "There ain't no profile to get! >_<";
				if( response.Length > 0 )
					responseString = $"```\n{e.Server.Config.CommandPrefix}setProfile {response.ToString()}";
				await e.SendReplySafe(responseString);

				dbContext.Dispose();
			};
			commands.Add(newCommand);

// !setProfile
			newCommand = new Command("setProfile");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Set your profile.";
			newCommand.ManPage = new ManPage("[--help]", "`[--help]` - Display options configured for this server.");
			newCommand.RequiredPermissions = PermissionType.Everyone;
			newCommand.OnExecute += async e => {
				if( !this.Client.IsPremium(e.Server) && !this.Client.IsTrialServer(e.Server.Id) )
				{
					await e.SendReplySafe("User profiles are a subscriber-only feature.");
					return;
				}

				if( !e.Server.Config.ProfileEnabled )
				{
					await e.SendReplySafe("User profiles are disabled on this server.");
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
				StringBuilder response = new StringBuilder();

				IEnumerable<ProfileOption> options = dbContext.ProfileOptions.AsQueryable().Where(o => o.ServerId == e.Server.Id).AsEnumerable().OrderBy(o => o.Order);
				int maxOptionLength = 0;
				foreach( ProfileOption option in options )
				{
					if( maxOptionLength < option.OptionAlt.Length )
						maxOptionLength = option.OptionAlt.Length;
				}

				MatchCollection emptyOptions = this.ProfileEmptyOptionRegex.Matches(e.TrimmedMessage);

				if( string.IsNullOrEmpty(e.TrimmedMessage) || (emptyOptions.Count > 0 && emptyOptions.Cast<Match>().Any(o => o.Value == "-h" || o.Value == "--help")) )
				{
					response.AppendLine("```md\nSet your profile fields with the following parameters:");
					foreach( ProfileOption o in options )
					{
						response.Append($"  [ {o.Option} ][ {o.OptionAlt}");
						response.Append(' ', maxOptionLength - o.OptionAlt.Length);
						response.AppendLine($" ] | {o.Label}");
					}

					response.AppendLine($"\nExample:\n  {e.Server.Config.CommandPrefix}{e.CommandId} --twitter @RheaAyase -y youtube.com/RheaAyase");
					response.AppendLine($"\nTo null one of the options you have already set, leave it empty:\n  {e.Server.Config.CommandPrefix}{e.CommandId} --twitter -y\n```");
					dbContext.Dispose();
					await e.SendReplySafe(response.ToString());
					return;
				}

				foreach( Match match in emptyOptions )
				{
					ProfileOption option = options.FirstOrDefault(o => o.Option == match.Value || o.OptionAlt == match.Value);
					if( option == null )
						continue;
					UserProfileOption userOption = dbContext.UserProfileOptions.AsQueryable().FirstOrDefault(o => o.ServerId == e.Server.Id && o.UserId == e.Message.Author.Id && o.Option == option.Option);
					if( userOption == null )
						continue;

					dbContext.UserProfileOptions.Remove(userOption);
				}

				MatchCollection matches = this.ProfileParamRegex.Matches(e.TrimmedMessage);
				foreach( Match match in matches )
				{
					string optionString = this.ProfileOptionRegex.Match(match.Value).Value;
					string value = match.Value.Substring(optionString.Length + 1).Replace('`','\'');
					if( value.Length >= UserProfileOption.ValueCharacterLimit )
					{
						await e.SendReplySafe($"`{optionString}` is too long! (It's {value.Length} characters while the limit is {UserProfileOption.ValueCharacterLimit})");
						dbContext.Dispose();
						return;
					}

					ProfileOption option = options.FirstOrDefault(o => o.Option == optionString || o.OptionAlt == optionString);
					if( option == null )
					{
						await e.SendReplySafe($"Unknown option: `{optionString}`");
						dbContext.Dispose();
						return;
					}

					UserProfileOption userOption = dbContext.UserProfileOptions.AsQueryable().FirstOrDefault(o => o.ServerId == e.Server.Id && o.UserId == e.Message.Author.Id && o.Option == option.Option);
					if( userOption == null )
					{
						userOption = new UserProfileOption(){
							ServerId = e.Server.Id,
							UserId = e.Message.Author.Id,
							Option = option.Option
						};
						dbContext.UserProfileOptions.Add(userOption);
					}

					userOption.Value = value;
				}

				dbContext.SaveChanges();

				await e.Channel.SendMessageAsync("", embed: GetProfileEmbed(dbContext, e.Server, e.Message.Author as SocketGuildUser));

				dbContext.Dispose();
			};
			commands.Add(newCommand);

			return commands;
		}

		private Embed GetProfileEmbed(ServerContext dbContext, Server server, SocketGuildUser user)
		{
			IEnumerable<ProfileOption> options = dbContext.ProfileOptions.AsQueryable().Where(o => o.ServerId == server.Id).AsEnumerable().OrderBy(o => o.Order);

			EmbedBuilder embedBuilder = new EmbedBuilder().WithThumbnailUrl(user.GetAvatarUrl())
				.WithAuthor($"{user.GetNickname()}'s profile on {server.Guild.Name}", server.Guild.IconUrl)
				.AddField("Username", user.GetUsername());

			SocketRole highestRole = user.Roles.Where(r => r.Color.RawValue != 0).OrderByDescending(r => r.Position).FirstOrDefault();
			if( highestRole != null )
				embedBuilder.Color = highestRole.Color;

			foreach( ProfileOption option in options )
			{
				UserProfileOption userOption = dbContext.UserProfileOptions.AsQueryable().FirstOrDefault(o => o.ServerId == server.Id && o.UserId == user.Id && o.Option == option.Option);
				if( userOption == null || string.IsNullOrWhiteSpace(userOption.Value) )
					continue;

				embedBuilder.AddField(option.Label, userOption.Value, option.IsInline);
			}

			return embedBuilder.Build();
		}

		private Embed GetValkyrjaEmbed(SocketGuildUser user)
		{
			EmbedBuilder embedBuilder = new EmbedBuilder().WithThumbnailUrl("https://valkyrja.app/img/valkyrja-geared-517p.png")
				.WithAuthor($"Kára", user.GetAvatarUrl())
				.AddField("Who am I?", "I'm a Bunny Valkyrie, my name is Kára. I help with community management, administration and moderation tasks to help create safe spaces for inclusive communities.", false)
				.AddField("Website", "[https://valkyrja.app](https://valkyrja.app)", true)
				.AddField("Purpose", "[Community Management](http://rhea-ayase.eu/articles/2017-04/Moderation-guidelines)", true)
				.AddField("Language", "[C#](https://en.wikipedia.org/wiki/C_Sharp_(programming_language\\))", true)
				.AddField("Platform", "[.NET Core](https://github.com/dotnet)", true)
				.AddField("License", "[MIT](https://github.com/RheaAyase/Valkyrja.discord/blob/master/LICENSE)", true)
				.AddField("Operating System", "[Fedora Linux](https://discord.gg/fedora)", true)
				.AddField("Server", "AMD Ryzen 3950x, 128GBs G.Skill memory and ~20TB raid5.")
				.AddField("Author", "A young woman who inspires the desolate white space of Linux world with the delicate C# letters of simplified artificial intelligence. Also a [Mountain Biker](https://rhea-ayase.eu/mtb) and a .NET Core QE Lead at Red Hat.")
				.AddField("Web-Author", "[Her fiancé](https://github.com/SpyTec), also a professional slacker.")
				.AddField("Questions?", $"Direct them to the [Valhalla]({GlobalConfig.DiscordInvite}), Valkyrja's support server.");

			SocketRole highestRole = user.Roles.Where(r => r.Color.RawValue != 0).OrderByDescending(r => r.Position).FirstOrDefault();
			if( highestRole != null )
				embedBuilder.Color = highestRole.Color;

			return embedBuilder.Build();
		}

		public Task Update(IValkyrjaClient iClient)
		{
			return Task.CompletedTask;
		}
	}
}

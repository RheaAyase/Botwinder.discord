﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading.Tasks;
using Botwinder.core;
using Botwinder.entities;
using Discord.WebSocket;
using guid = System.UInt64;

namespace Botwinder.modules
{
	public class Karma: IModule
	{
		private const string KarmaDisabledString = "Karma is disabled on this server.";

		private BotwinderClient Client;
		private readonly Regex RegexKarma = new Regex(".*(?<!\\\\)(thank(?!sgiving)|thx|ʞuɐɥʇ|danke|vielen dank|gracias|merci(?!al)|grazie|arigato|dziękuję|dziekuje|obrigad).*", RegexOptions.Compiled | RegexOptions.IgnoreCase);


		public Func<Exception, string, guid, Task> HandleException{ get; set; }
		public bool DoUpdate{ get; set; } = false;

		public List<Command> Init(IBotwinderClient iClient)
		{
			this.Client = iClient as BotwinderClient;
			List<Command> commands = new List<Command>();

			this.Client.Events.MessageReceived += OnMessageReceived;

// !cookies
			Command newCommand = new Command("cookies");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Check how many cookies you've got.";
			newCommand.IsPremiumServerwideCommand = true;
			newCommand.RequiredPermissions = PermissionType.Everyone;
			newCommand.OnExecute += async e => {
				if( !e.Server.Config.KarmaEnabled )
				{
					await e.SendReplySafe(KarmaDisabledString);
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
				UserData userData = dbContext.GetOrAddUser(e.Server.Id, e.Message.Author.Id);

				await e.SendReplySafe(string.Format("Hai **{0}**, you have {1} {2}!\nYou can {4} one with the `{3}{4}` command, or you can give a {5} to your friend using `{3}give @friend`",
					e.Message.Author.GetNickname(), userData.KarmaCount,
					(userData.KarmaCount == 1 ? e.Server.Config.KarmaCurrencySingular : e.Server.Config.KarmaCurrency),
					e.Server.Config.CommandPrefix, e.Server.Config.KarmaConsumeCommand,
					e.Server.Config.KarmaCurrencySingular));

				dbContext.Dispose();
			};
			commands.Add(newCommand);

// !nom
			newCommand = new Command("nom");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Eat one of your cookies!";
			newCommand.IsPremiumServerwideCommand = true;
			newCommand.RequiredPermissions = PermissionType.Everyone;
			newCommand.OnExecute += async e => {
				if( !e.Server.Config.KarmaEnabled )
				{
					await e.SendReplySafe(KarmaDisabledString);
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
				UserData userData = dbContext.GetOrAddUser(e.Server.Id, e.Message.Author.Id);

				if( userData.KarmaCount <= 0 )
				{
					await e.SendReplySafe(string.Format("Umm... I'm sorry **{0}** you don't have any {1} left =(",
						e.Message.Author.GetNickname(), e.Server.Config.KarmaCurrency));

					dbContext.Dispose();
					return;
				}

				userData.KarmaCount -= 1;
				dbContext.SaveChanges();

				await e.SendReplySafe(string.Format("**{0}** just {1} one of {2} {3}! {4} {5} left.",
					e.Message.Author.GetNickname(), e.Server.Config.KarmaConsumeVerb,
					(this.Client.IsGlobalAdmin(e.Message.Author.Id) ? "her" : "their"), e.Server.Config.KarmaCurrency,
					(this.Client.IsGlobalAdmin(e.Message.Author.Id) ? "She has" : "They have"), userData.KarmaCount));// Because i can :P

				dbContext.Dispose();
			};
			commands.Add(newCommand);

// !give
			newCommand = new Command("give");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Give one of your cookies to a friend =] (use with their @mention as a parameter)";
			newCommand.IsPremiumServerwideCommand = true;
			newCommand.RequiredPermissions = PermissionType.Everyone;
			newCommand.OnExecute += async e => {
				if( !e.Server.Config.KarmaEnabled )
				{
					await e.SendReplySafe(KarmaDisabledString);
					return;
				}

				UserData userData = await this.Client.DbAccessManager.GetReadOnlyUserData(e.Server.Id, e.Message.Author.Id);
				if( userData.KarmaCount == 0 )
				{
					await e.SendReplySafe(string.Format("Umm... I'm sorry **{0}**, you don't have any {1} left =(",
						e.Message.Author.GetNickname(), e.Server.Config.KarmaCurrency));

					return;
				}

				if( e.Message.MentionedUsers == null || !e.Message.MentionedUsers.Any() || e.Message.MentionedUsers.Count() > e.Server.Config.KarmaLimitMentions )
				{
					await e.SendReplySafe(string.Format("You have to @mention your friend who will receive the {0}. You can mention up to {1} people at the same time.",
						e.Server.Config.KarmaCurrencySingular, e.Server.Config.KarmaLimitMentions));

					return;
				}

				int count = 0;
				StringBuilder userNames = new StringBuilder();
				List<guid> mentionedUserIds = this.Client.GetMentionedUserIds(e);
				await this.Client.DbAccessManager.ForEachModifyUserData(e.Server.Id, mentionedUserIds, (mentionedId, mentionedData) => {
					if( userData.KarmaCount == 0 )
						return true;

					userData.KarmaCount--;
					mentionedData.KarmaCount++;
					userNames.Append((count++ == 0 ? "" : count == mentionedUserIds.Count ? ", and " : ", ") + (e.Server.Guild.GetUser(mentionedData.UserId)?.GetNickname() ?? "Unknown"));

					return false;
				});

				await this.Client.DbAccessManager.ModifyUserData(userData.ServerId, userData.UserId, u => u.KarmaCount = userData.KarmaCount);

				string response = string.Format("**{0}** received a {1} of friendship from **{2}** =]",
					userNames, e.Server.Config.KarmaCurrencySingular, e.Message.Author.GetNickname());
				if( count < mentionedUserIds.Count )
					response += "\nBut I couldn't give out more, as you don't have any left =(";

				await e.SendReplySafe(response);
			};
			commands.Add(newCommand);


			return commands;
		}

		private async Task OnMessageReceived(SocketMessage message)
		{
			if( !this.Client.GlobalConfig.ModuleUpdateEnabled )
				return;

			Server server;
			if( !(message.Channel is SocketTextChannel channel) || !this.Client.Servers.ContainsKey(channel.Guild.Id) || (server = this.Client.Servers[channel.Guild.Id]) == null )
				return;
			if( !(message.Author is SocketGuildUser user) || message.Author.IsBot )
				return;
			if( !this.Client.IsPremium(server) && !this.Client.IsTrialServer(server.Id) )
				return;
			if( !server.Config.KarmaEnabled || message.MentionedUsers == null || !message.MentionedUsers.Any() || !this.RegexKarma.Match(message.Content).Success )
				return;

			ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);

			UserData userData = dbContext.GetOrAddUser(server.Id, user.Id);
			IEnumerable<UserData> mentionedUserData = message.MentionedUsers.Select(u => dbContext.GetOrAddUser(server.Id, u.Id));
			int count = mentionedUserData.Count();

			if( (count > server.Config.KarmaLimitMentions || userData.LastThanksTime.AddMinutes(server.Config.KarmaLimitMinutes) > DateTimeOffset.UtcNow) )
			{
				if( server.Config.KarmaLimitResponse )
					await message.Channel.SendMessageSafe("You're thanking too much ó_ò");
				return;
			}

			int thanked = 0;
			StringBuilder userNames = new StringBuilder();
			foreach(UserData mentionedUser in mentionedUserData)
			{
				if( mentionedUser.UserId != user.Id )
				{
					mentionedUser.KarmaCount++;

					userNames.Append((thanked++ == 0 ? "" : thanked == count ? ", and " : ", ") + server.Guild.GetUser(mentionedUser.UserId).GetNickname());
				}
			}

			if( thanked > 0 )
			{
				userData.LastThanksTime = DateTime.UtcNow;
				dbContext.SaveChanges();
				if( server.Config.IgnoreEveryone )
					userNames = userNames.Replace("@everyone", "@-everyone").Replace("@here", "@-here");
				await this.Client.SendRawMessageToChannel(channel, string.Format("**{0}** received a _thank you_ {1}!", userNames, server.Config.KarmaCurrencySingular));
			}

			dbContext.Dispose();
		}

		public Task Update(IBotwinderClient iClient)
		{
			return Task.CompletedTask;
		}
	}
}

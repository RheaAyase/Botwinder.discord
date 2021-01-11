﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Valkyrja.core;
using Valkyrja.entities;
using Discord.Net;
using Discord.WebSocket;
using Microsoft.EntityFrameworkCore.ChangeTracking.Internal;
using Microsoft.EntityFrameworkCore.Internal;
using guid = System.UInt64;

namespace Valkyrja.modules
{
	public class Experience: IModule
	{
		private const string ExpDisabledString = "Experience and levels are disabled on this server.";
		private const string LevelupString = "<@{0}>, you've gained the level `{1}`";
		private const string LevelString = "Your current level is `{0}` (`{1}`)";
		private const string LevelNullString = "Your current level is `{0}`";
		private const string MessagesToLevel = "\nYou're {0} messages away from the next!";
		private const string ImagesToLevel = "\nYou're {0} images away from the next!";
		private const string ThingsToLevel = "\nYou're {0} messages or {1} images away from the next!";

		private ValkyrjaClient Client;


		public Func<Exception, string, guid, Task> HandleException{ get; set; }
		public bool DoUpdate{ get; set; } = false;

		public List<Command> Init(IValkyrjaClient iClient)
		{
			this.Client = iClient as ValkyrjaClient;
			List<Command> commands = new List<Command>();

			this.Client.Events.MessageReceived += OnMessageReceived;

// !lvl
			Command newCommand = new Command("lvl");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Find out what's your level!";
			newCommand.ManPage = new ManPage("", "");
			newCommand.IsPremiumServerwideCommand = true;
			newCommand.RequiredPermissions = PermissionType.Everyone;
			newCommand.OnExecute += async e => {
				if( !e.Server.Config.ExpEnabled )
				{
					await e.SendReplySafe(ExpDisabledString);
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
				UserData userData = dbContext.GetOrAddUser(e.Server.Id, e.Message.Author.Id);

				string response = string.Format(LevelNullString, userData.Level);

				SocketRole role = null;
				if( userData.Level > 0 &&
				    (role = e.Server.Roles.Values.Where(r => r.ExpLevel == userData.Level)
					    .Select(r => e.Server.Guild.GetRole(r.RoleId)).FirstOrDefault()) != null )
				{
					response = string.Format(LevelString, role.Name, userData.Level);
				}

				Int64 expAtLevel = GetTotalExpAtLevel(e.Server.Config.BaseExpToLevelup, userData.Level + 1);
				Int64 expToLevel = expAtLevel - userData.Exp;

				if( e.Server.Config.ExpPerMessage != 0 && e.Server.Config.ExpPerAttachment != 0 )
					response += string.Format(ThingsToLevel, expToLevel / e.Server.Config.ExpPerMessage, expToLevel / e.Server.Config.ExpPerAttachment);
				else if( e.Server.Config.ExpPerMessage != 0 )
					response += string.Format(MessagesToLevel, expToLevel / e.Server.Config.ExpPerMessage);
				else if( e.Server.Config.ExpPerAttachment != 0 )
					response += string.Format(ImagesToLevel, expToLevel / e.Server.Config.ExpPerAttachment);

				await e.SendReplySafe(response);
				dbContext.Dispose();
			};
			commands.Add(newCommand);

// !lockExp
			newCommand = new Command("lockExp");
			newCommand.Type = CommandType.Standard;
			newCommand.Description = "Locks, or unlocks, someones experience and therefore also activity role assignment.";
			newCommand.ManPage = new ManPage("<@user>", "`<@user>` - User mentions or IDs who to lock out of the experience system.");
			newCommand.IsPremiumServerwideCommand = true;
			newCommand.RequiredPermissions = PermissionType.ServerOwner | PermissionType.Admin | PermissionType.Moderator | PermissionType.SubModerator;
			newCommand.OnExecute += async e => {
				if( string.IsNullOrEmpty(e.TrimmedMessage) )
				{
					await e.SendReplySafe("A who?");
					return;
				}

				ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
				List<UserData> mentionedUserData = this.Client.GetMentionedUsersData(dbContext, e);
				if( !mentionedUserData.Any() )
				{
					dbContext.Dispose();
					await e.SendReplySafe("A who?");
					return;
				}
				foreach( UserData userData in mentionedUserData )
				{
					userData.ExpLocked = !userData.ExpLocked;
				}

				string response = "";
				if( mentionedUserData.Any(u => u.ExpLocked) )
					response += "I've locked " + mentionedUserData.Where(u => u.ExpLocked).Select(u => $"<@{u.UserId}>").ToNames("", "");
				if( mentionedUserData.Any(u => !u.ExpLocked) )
					response += "I've unlocked " + mentionedUserData.Where(u => !u.ExpLocked).Select(u => $"<@{u.UserId}>").ToNames("", "");

				dbContext.SaveChanges();
				dbContext.Dispose();

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
			if( !(message.Channel is SocketTextChannel channel) ||
			    !this.Client.Servers.ContainsKey(channel.Guild.Id) ||
			    (server = this.Client.Servers[channel.Guild.Id]) == null )
				return;
			if( message.Author.IsBot || !(message.Author is SocketGuildUser user) ||
			    (!server.Config.ExpEnabled && server.Config.ExpMemberMessages == 0) )
				return;
			if( !this.Client.IsPremium(server) && !this.Client.IsTrialServer(server.Id) )
				return;
			if( server.Config.ExpIgnoredChannels && server.IgnoredChannels.Contains(message.Channel.Id) )
				return;

			ServerContext dbContext = ServerContext.Create(this.Client.DbConnectionString);
			UserData userData = dbContext.GetOrAddUser(server.Id, user.Id);
			if( userData.ExpLocked )
			{
				dbContext.Dispose();
				return;
			}

			if( !string.IsNullOrEmpty(message.Content) )
				userData.CountMessages++;
			if( message.Attachments.Any() )
				userData.CountAttachments++;

			try
			{
				if( server.Config.ExpMemberRoleId != 0 && server.Config.ExpMemberMessages > 0 && userData.CountMessages > server.Config.ExpMemberMessages && user.Roles.All(r => r.Id != server.Config.ExpMemberRoleId) )
				{
					SocketRole memberRole = server.Guild.GetRole(server.Config.ExpMemberRoleId);
					if( memberRole != null )
						await user.AddRoleAsync(memberRole);
				}

				if( server.Config.ExpEnabled && (server.Config.ExpMaxLevel == 0 || userData.Level < server.Config.ExpMaxLevel) )
				{

					userData.Exp = server.Config.ExpPerMessage * userData.CountMessages + server.Config.ExpPerAttachment * userData.CountAttachments;

					// Recalculate level and assign appropriate roles if it changed.
					Int64 newLvl = GetLvlFromExp(server.Config.BaseExpToLevelup, userData.Exp);
					if( newLvl != userData.Level )
					{
						if( newLvl > userData.Level )
						{
							userData.KarmaCount += server.Config.KarmaPerLevel * (newLvl - userData.Level);
						}

						if( server.Config.ExpAdvanceUsers )
						{
							SocketRole highestRole = null;
							RoleConfig highestRoleConfig = null;
							RoleConfig roleConfig = null;
							foreach( SocketRole role in user.Roles.Where(r => server.Roles.ContainsKey(r.Id) && (roleConfig = server.Roles[r.Id]).ExpLevel > 0) )
							{
								if( highestRoleConfig == null || (roleConfig != null && roleConfig.ExpLevel > highestRoleConfig.ExpLevel) )
								{
									highestRoleConfig = roleConfig;
									highestRole = role;
								}
							}

							if( highestRoleConfig != null && highestRole != null && highestRoleConfig.ExpLevel > userData.Level )
							{
								newLvl = highestRoleConfig.ExpLevel;
								userData.Exp = GetTotalExpAtLevel(server.Config.BaseExpToLevelup, newLvl);
								userData.CountMessages = userData.Exp / server.Config.ExpPerMessage;
								userData.CountAttachments = 1;
							}
						}

						bool IsRoleToAssign(RoleConfig r)
						{
							return r.ExpLevel != 0 && ((server.Config.ExpCumulativeRoles && r.ExpLevel <= newLvl) || (!server.Config.ExpCumulativeRoles && r.ExpLevel == newLvl));
						}

						IEnumerable<SocketRole> rolesToAssign = server.Roles.Values.Where(IsRoleToAssign).Select(r => server.Guild.GetRole(r.RoleId)).Where(r => r != null);
						if( rolesToAssign.Any() )
							await user.AddRolesAsync(rolesToAssign);

						bool IsRoleToRemove(RoleConfig r)
						{
							return r.ExpLevel != 0 && ((server.Config.ExpCumulativeRoles && r.ExpLevel > newLvl) || (!server.Config.ExpCumulativeRoles && r.ExpLevel != newLvl && rolesToAssign.Any()));
							//rolesToAssign.Any() to not remove roles if we're not on a new role-giving level.
						}

						List<SocketRole> rolesToRemove = server.Roles.Values.Where(IsRoleToRemove).Select(r => server.Guild.GetRole(r.RoleId)).Where(r => r != null).ToList();
						foreach( SocketRole roleToRemove in rolesToRemove )
							await user.RemoveRoleAsync(roleToRemove);

						if( newLvl > userData.Level && server.Config.ExpAnnounceLevelup )
						{
							SocketRole role = server.Roles.Values.Where(r => r.ExpLevel == newLvl)
								.Select(r => server.Guild.GetRole(r.RoleId)).FirstOrDefault();
							await channel.SendMessageSafe(string.Format(LevelupString, user.Id, role?.Name ?? newLvl.ToString()));
						}

						userData.Level = newLvl;
					}
				}

				dbContext.SaveChanges();
			}
			catch( HttpException e )
			{
				await server.HandleHttpException(e, $"This happened in <#{channel.Id}> when trying to assign a role.");
			}
			catch( Exception e )
			{
				await this.HandleException(e, "Levelup error", server.Id);
			}
			finally
			{
				dbContext.Dispose();
			}
		}

		public Task Update(IValkyrjaClient iClient)
		{
			return Task.CompletedTask;
		}

		[MethodImpl(MethodImplOptions.AggressiveInlining)]
		private Int64 GetExpToLevel(Int64 baseExp, Int64 lvl)
		{
			//exp = base * lvl * (lvl + 1)
			return baseExp * (lvl) * (lvl + 1);
		}
		private Int64 GetTotalExpAtLevel(Int64 baseExp, Int64 lvl)
		{
			if( lvl <= 0 )
				return 0;
			if( lvl == 1 )
				return GetExpToLevel(baseExp, lvl);

			return GetExpToLevel(baseExp, lvl) + GetTotalExpAtLevel(baseExp, lvl-1);
		}

		private Int64 GetLvlFromExp(Int64 baseExp, Int64 currentExp)
		{
			Int64 lvl = 0;
			Int64 expAtLvl = 0;
			while( (expAtLvl += GetExpToLevel(baseExp, ++lvl)) < currentExp ) ;
			return lvl - 1;
		}
	}
}

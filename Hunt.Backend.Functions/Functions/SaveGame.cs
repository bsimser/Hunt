using System.Linq;
using System.Net;
using System.Net.Http;
using System.Threading.Tasks;
using System.Collections.Generic;
using System;
using Microsoft.Azure.WebJobs;
using Microsoft.Azure.WebJobs.Extensions.Http;
using Microsoft.Azure.WebJobs.Host;
using Microsoft.ApplicationInsights.DataContracts;

using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

using Hunt.Common;
using Hunt.Backend.Analytics;
using System.Text;

namespace Hunt.Backend.Functions
{
	public static class SaveGame
	{
		[FunctionName(nameof(SaveGame))]

		public static HttpResponseMessage Run([HttpTrigger(AuthorizationLevel.Anonymous, "post", Route = nameof(SaveGame))]
		HttpRequestMessage req, TraceWriter log)
		{
			using (var analytic = new Analytic(new RequestTelemetry
			{
				Name = nameof(SaveGame)
			}))
			{
				var json = req.Content.ReadAsStringAsync().Result;
				var jobject = JsonConvert.DeserializeObject<JObject>(json);

				var action = jobject["action"].ToString();
				var arguments = jobject["arguments"].ToObject<Dictionary<string, string>>();
				var game = jobject["game"].ToObject<Game>();

				//Need to validate this player is not already part of another ongoing game or the coordinator of this game
				if (game.EntryCode == null)
				{
					//Let's hope this is generally random. Best to confirm the code is not already in used but I'm lazy
					game.EntryCode = Math.Abs(game.Id.GetHashCode()).ToString().Substring(0, 6);
				}

				Game savedGame = null;

				try
				{
					using (var client = new CosmosDataService())
					{
						if (!game.IsPersisted)
						{
							client.InsertItemAsync(game).Wait();
							SendTargetedNotifications(game, action, arguments);
						}
						else
						{
							var existingGame = client.GetItemAsync<Game>(game.Id).Result;
							if (existingGame.TS != game.TS)
								return req.CreateErrorResponse(HttpStatusCode.Conflict, "Unable to save game - version conflict. Please pull the latest version and reapply your changes.");

							if (action == GameUpdateAction.EndGame && existingGame.HasEnded)
								return req.CreateResponse(HttpStatusCode.OK);

							bool isWinningAcquisition = false;
							if (action == GameUpdateAction.AcquireTreasure)
							{
								//Need to evaluate the game first before we save as there might be a winner
								var teamId = arguments["teamId"];
								isWinningAcquisition = game.EvaluateGameForWinner(teamId);

								if (isWinningAcquisition)
								{
									game.EndDate = DateTime.UtcNow;
									var teams = game.Teams.OrderByDescending(t => t.TotalPoints).ToArray();

									if (teams[0].TotalPoints == teams[1].TotalPoints)
										game.WinnningTeamId = null; //Draw
									else
										game.WinnningTeamId = teams[0].Id;
								}
							}
							client.UpdateItemAsync(game).Wait();

							if (action == GameUpdateAction.StartGame)
							{
								var http = new HttpClient();
								var minutes = 2;
								var url = $"https://huntapp.azurewebsites.net/api/SetEndGame?gameId={game.Id}&minutes={minutes}";
								var content = new StringContent(string.Empty, Encoding.UTF8, "application/json");
								var resp = http.PostAsync(url, content).Result;
							}

							if (isWinningAcquisition)
							{
								SendTargetedNotifications(game, GameUpdateAction.EndGame, arguments);
							}
							else
							{
								SendTargetedNotifications(game, action, arguments);
							}
						}

						if (action == GameUpdateAction.StartGame)
						{
							//Schedule timer function here and pass in GameID
							//When the timer function fires, use the GameID to look up the game
						}

						savedGame = client.GetItemAsync<Game>(game.Id).Result; //Comment out at some point if not needed
					}

					return req.CreateResponse(HttpStatusCode.OK, savedGame);
				}
				catch (Exception e)
				{
					// track exceptions that occur
					analytic.TrackException(e);

					return req.CreateErrorResponse(HttpStatusCode.BadRequest, e.Message, e);
				}
			}
		}

		static async Task SendTargetedNotifications(Game game, string action, Dictionary<string, string> args)
		{
			var push = new PushManager();
			string title = null;
			string message = null;
			List<Player> players = new List<Player>();
			bool silentNotifyAllPlayers = false;

			switch (action)
			{
				case GameUpdateAction.Create:
					{
						//Nothing really to do here
						break;
					}
				case GameUpdateAction.StartGame:
					{
						//Notify all game players
						title = "Hunt Game has started!";
						message = $"Your hunt game has started! You have {game.DurationInMinutes}min to acquire all treasures - good luck and godspeed!";

						players.AddRange(game.GetAllPlayers());
						break;
					}
				case GameUpdateAction.EndGame:
					{
						//Notify all players + coordinator
						var team = game.Teams.Get(game.WinnningTeamId);
						title = "Your hunt game has ended";
						message = "Game over. This game ended in a draw.";

						if (team != null)
							message = $"Game Over. Team {team.Name} is the winner. Thanks for playing!";

						players.AddRange(game.GetAllPlayers());

						break;
					}
				case GameUpdateAction.JoinTeam:
					{
						silentNotifyAllPlayers = true;
						var player = game.GetPlayer(args["playerId"]);
						var team = game.Teams.Get(args["teamId"]);

						if (team == null || player == null)
							break;

						//Notify team players
						title = "New teammate :)";
						message = $"{player.Alias} has joined your team. You should say hello.";

						players.AddRange(team.Players);
						players.Remove(player);

						break;
					}
				case GameUpdateAction.LeaveTeam:
					{
						silentNotifyAllPlayers = true;
						var playerAlias = args["playerAlias"];
						var team = game.Teams.Get(args["teamId"]);

						if (team == null || playerAlias == null)
							break;

						//Notify team players
						title = "Someone left your team :(";
						message = $"{playerAlias} had to leave your team - they're sorry.";
						players.AddRange(team.Players);

						break;
					}
				case GameUpdateAction.AcquireTreasure:
					{
						var team = game.Teams.Get(args["teamId"]);
						var acquiredTreasure = team.AcquiredTreasure.Get(args["acquiredTreasureId"]);
						var player = game.GetPlayer(acquiredTreasure.PlayerId);
						var treasure = game.Treasures.Get(acquiredTreasure.TreasureId);

						if (team == null || player == null || acquiredTreasure == null)
							break;

						//Notify team players
						title = $"Treasure acquired for {treasure.Points} points!";
						message = $"{player.Alias} just acquired the '{treasure.Hint}' treasure";
						players.AddRange(team.Players);
						players.Remove(player);

						silentNotifyAllPlayers = true;
						break;
					}
				default:
					silentNotifyAllPlayers = true;
					break;
			}

			var devices = new string[0];
			if (title != null && message != null && players.Count > 0)
			{
				devices = players.Where(pl => pl.InstallId != null).Select(pl => pl.InstallId).ToArray();

				if (devices.Length > 0)
					await push.SendNotification(title, message, devices,
						new Dictionary<string, string> { { "gameId", game.Id } });
			}

			if (silentNotifyAllPlayers)
			{
				var allPlayers = game.GetAllPlayers();
				var allDevices = allPlayers.Where(pl => pl.InstallId != null && !devices.Contains(pl.InstallId)).Select(pl => pl.InstallId).ToArray();

				if (allDevices.Length > 0)
				{
					await push.SendNotification("", "", allDevices,
						new Dictionary<string, string> { { "content-available", "1" }, { "gameId", game.Id } });
				}
			}
		}

		/// <summary>
		/// Not used right now
		/// </summary>
		/// <param name="game"></param>
		static void CreateAudiences(Game game, Analytic analytic)
		{
			//Configure the proper Push Audiences in Mobile Center so notifications can be sent to all in a game or team
			var p = new PushManager();
			var success = p.CreateAudience(HuntAudience.GameId, game.Id).Result;

			if (success)
			{
				foreach (var team in game.Teams)
				{
					var good = p.CreateAudience(HuntAudience.TeamId, team.Id).Result;

					if (!good)
						throw new Exception("Unable to properly configure push notifications");
				}
			}
			else
			{
				var e = new Exception("Unable to properly configure push notifications");

				// track exceptions that occur
				analytic.TrackException(e);

				throw e;
			}
		}
	}
}
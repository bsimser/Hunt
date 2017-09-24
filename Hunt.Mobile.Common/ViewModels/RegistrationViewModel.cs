using System;
using Hunt.Common;
using Microsoft.Azure.Mobile;

namespace Hunt.Mobile.Common
{
	public class RegistrationViewModel : BaseViewModel
	{
		string _email;// = $"{Device.RuntimePlatform.ToLower()}@microsoft.com";
		public string Email
		{
			get { return _email; }
			set { SetPropertyChanged(ref _email, value); SetPropertyChanged(nameof(CanContinue)); }
		}

		string _alias;// = $"{Device.RuntimePlatform} Player";
		public string Alias
		{
			get { return _alias; }
			set { SetPropertyChanged(ref _alias, value); SetPropertyChanged(nameof(CanContinue)); }
		}

		string _avatar;// = "https://s.gravatar.com/avatar/fc29d876d1ae49003cbc76a43f456d9b?s=200";
		public string Avatar
		{
			get { return _avatar; }
			set { SetPropertyChanged(ref _avatar, value); }
		}

		public bool CanContinue { get { return !string.IsNullOrWhiteSpace(Email) && !string.IsNullOrWhiteSpace(Alias); } }

		public void RegisterPlayer()
		{
			if(string.IsNullOrWhiteSpace(Email) || string.IsNullOrWhiteSpace(Alias))
				throw new Exception("Please specify an email and alias.");

			var email = Email; //We toy with a copy because the two-way binding will cause the TextChanged event to fire
			var split = email.Split('@');
			if(split.Length == 2)
			{
				if(split[1].ToLower() == "hbo.com") //GoT character
				{
					//Randomize the email (which serves as the primary key) so it doesn't conflict w/ other demo games
					var rand = Guid.NewGuid().ToString().Substring(0, 7).ToLower();
					email = $"{split[0]}-{rand}@{split[1]}";
				}
			}

			var player = new Player
			{
				Avatar = Avatar,
				Email = email.Trim(),
				Alias = Alias.Trim(),
				InstallId = MobileCenter.GetInstallIdAsync().Result.ToString(),
			};

			App.Instance.SetPlayer(player);
		}

		public void Reset()
		{
			Alias = null;
			Email = null;
		}
	}
}
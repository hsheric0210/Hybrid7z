using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using System.Threading.Tasks;

namespace Hybrid7z
{
	public static class StringExtension
	{
		public static string WithNamespace(this string message, string? @namespace = null)
		{
			if (@namespace != null)
				message = $"[{@namespace}] {message}";
			return message;
		}

		public static string WithNamespaceAndTitle(this string message, string? @namespace = null)
		{
			if (@namespace != null)
				message = $"[{@namespace}] {message}";
			Console.Title = message;
			return message;
		}
	}
}

using System;
using System.Collections.Generic;
using System.Text;
using Alloy.Sdk.Web.Html;

namespace Alloy.Sdk.Web
{
	// What a submitted form turns into: the request to make
	public sealed class FormSubmission
	{
		public Uri Url;
		public string Method = "GET";
		public byte[] Body;
		public string ContentType;

		public override string ToString() => Method == "GET" ? Url.ToString() : $"{Method} {Url} ({Body?.Length ?? 0} bytes)";
	}

	// HTML forms without scripts: the fields that count, their values, URL encoding, and GET or POST submission
	public static class Forms
	{
		// The form an element belongs to, by ancestry or by its form attribute
		public static Element FormOf(Element element)
		{
			if (element == null)
				return null;
			string owner = element.GetAttribute("form");
			if (owner != null)
			{
				Node root = element;
				while (root.Parent != null)
					root = root.Parent;
				foreach (Element candidate in root.DescendantElements())
				{
					if (candidate.TagName == "form" && candidate.Id == owner)
						return candidate;
				}
			}
			for (Node node = element; node != null; node = node.Parent)
			{
				if (node is Element candidate && candidate.TagName == "form")
					return candidate;
			}
			return null;
		}

		public static bool IsTextField(Element element)
		{
			if (element == null)
				return false;
			if (element.TagName == "textarea")
				return true;
			if (element.TagName != "input")
				return false;
			string type = (element.GetAttribute("type") ?? "text").ToLowerInvariant();
			return type is "text" or "search" or "email" or "url" or "password" or "number" or "tel";
		}

		public static bool IsSubmitButton(Element element)
		{
			if (element == null)
				return false;
			if (element.TagName == "button")
			{
				string buttonType = (element.GetAttribute("type") ?? "submit").ToLowerInvariant();
				return buttonType == "submit";
			}
			if (element.TagName != "input")
				return false;
			string type = (element.GetAttribute("type") ?? "text").ToLowerInvariant();
			return type == "submit" || type == "image";
		}

		public static bool IsCheckable(Element element)
		{
			if (element == null || element.TagName != "input")
				return false;
			string type = (element.GetAttribute("type") ?? "text").ToLowerInvariant();
			return type == "checkbox" || type == "radio";
		}

		// The control's current value: what the user typed sits in the value attribute, a textarea's in its text until then
		public static string ValueOf(Element element)
		{
			if (element.TagName == "textarea")
				return element.GetAttribute("value") ?? element.TextContent;
			if (element.TagName == "select")
			{
				Element chosen = null;
				foreach (Element option in element.ElementsByTagName("option"))
				{
					if (chosen == null || option.HasAttribute("selected"))
						chosen = option;
					if (option.HasAttribute("selected"))
						break;
				}
				return chosen == null ? "" : chosen.GetAttribute("value") ?? chosen.TextContent.Trim();
			}
			return element.GetAttribute("value") ?? "";
		}

		public static void SetValue(Element element, string value) => element.SetAttribute("value", value);

		// Clicking a checkbox toggles it; a radio button turns its group off and itself on
		public static void Toggle(Element element)
		{
			string type = (element.GetAttribute("type") ?? "").ToLowerInvariant();
			if (type == "checkbox")
			{
				if (element.HasAttribute("checked"))
					element.RemoveAttribute("checked");
				else
					element.SetAttribute("checked", "");
				return;
			}
			if (type != "radio")
				return;
			string name = element.GetAttribute("name");
			Element form = FormOf(element);
			IEnumerable<Element> group = form != null ? form.DescendantElements() : new[] { element };
			foreach (Element other in group)
			{
				if (other != element && other.TagName == "input" && name != null && other.GetAttribute("name") == name)
					other.RemoveAttribute("checked");
			}
			element.SetAttribute("checked", "");
		}

		// Clicking a select moves to the next option, round and round; there is no dropdown to open
		public static void Advance(Element select)
		{
			List<Element> options = new List<Element>(select.ElementsByTagName("option"));
			if (options.Count == 0)
				return;
			int selected = Math.Max(0, options.FindIndex(o => o.HasAttribute("selected")));
			foreach (Element option in options)
				option.RemoveAttribute("selected");
			options[(selected + 1) % options.Count].SetAttribute("selected", "");
		}

		// The successful controls of a form in tree order, plus the submitter's own name and value
		public static List<KeyValuePair<string, string>> Collect(Element form, Element submitter)
		{
			List<KeyValuePair<string, string>> data = new List<KeyValuePair<string, string>>();
			foreach (Element control in form.DescendantElements())
			{
				string name = control.GetAttribute("name");
				if (string.IsNullOrEmpty(name) || control.HasAttribute("disabled"))
					continue;
				switch (control.TagName)
				{
					case "input":
					{
						string type = (control.GetAttribute("type") ?? "text").ToLowerInvariant();
						if (type == "submit" || type == "image" || type == "button" || type == "reset" || type == "file")
							break;
						if ((type == "checkbox" || type == "radio") && !control.HasAttribute("checked"))
							break;
						data.Add(new KeyValuePair<string, string>(name, type == "checkbox" || type == "radio" ? control.GetAttribute("value") ?? "on" : ValueOf(control)));
						break;
					}
					case "textarea":
					case "select":
						data.Add(new KeyValuePair<string, string>(name, ValueOf(control)));
						break;
				}
			}
			if (submitter != null && IsSubmitButton(submitter) && !string.IsNullOrEmpty(submitter.GetAttribute("name")))
				data.Add(new KeyValuePair<string, string>(submitter.GetAttribute("name"), submitter.GetAttribute("value") ?? ""));
			return data;
		}

		// application/x-www-form-urlencoded: spaces as plus signs, everything outside the safe set percent-encoded as UTF-8
		public static string Encode(IEnumerable<KeyValuePair<string, string>> data)
		{
			StringBuilder text = new StringBuilder();
			foreach (KeyValuePair<string, string> pair in data)
			{
				if (text.Length > 0)
					text.Append('&');
				Escape(text, pair.Key);
				text.Append('=');
				Escape(text, pair.Value ?? "");
			}
			return text.ToString();
		}

		private static void Escape(StringBuilder text, string value)
		{
			foreach (byte b in Encoding.UTF8.GetBytes(value))
			{
				char c = (char)b;
				if ((c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c == '-' || c == '_' || c == '.' || c == '*')
					text.Append(c);
				else if (c == ' ')
					text.Append('+');
				else
					text.Append('%').Append(b.ToString("X2"));
			}
		}

		// The request a submission makes: GET puts the data in the query, POST in a urlencoded body; null when the control has no form
		public static FormSubmission Submit(Element control, Uri pageUrl)
		{
			Element form = control?.TagName == "form" ? control : FormOf(control);
			if (form == null)
				return null;
			Element submitter = control != null && IsSubmitButton(control) ? control : null;
			if (submitter == null)
			{
				foreach (Element candidate in form.DescendantElements())
				{
					if (IsSubmitButton(candidate))
					{
						submitter = candidate;
						break;
					}
				}
			}
			string method = (submitter?.GetAttribute("formmethod") ?? form.GetAttribute("method") ?? "get").Trim().ToUpperInvariant();
			if (method != "POST")
				method = "GET";
			string action = submitter?.GetAttribute("formaction") ?? form.GetAttribute("action");
			Uri target;
			if (string.IsNullOrWhiteSpace(action))
				target = pageUrl;
			else if (!Uri.TryCreate(pageUrl, action.Trim(), out target) && !Uri.TryCreate(action.Trim(), UriKind.Absolute, out target))
				return null;
			if (target == null)
				return null;
			string encoded = Encode(Collect(form, submitter));
			FormSubmission submission = new FormSubmission { Method = method };
			if (method == "GET")
			{
				UriBuilder builder = new UriBuilder(target) { Query = encoded };
				submission.Url = builder.Uri;
			}
			else
			{
				submission.Url = target;
				submission.Body = Encoding.ASCII.GetBytes(encoded);
				submission.ContentType = "application/x-www-form-urlencoded";
			}
			return submission;
		}
	}
}

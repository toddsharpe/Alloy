namespace System.Collections
{
	// C# binds foreach over arrays through these even though it lowers the loop to indexing
	public interface IEnumerable
	{
		IEnumerator GetEnumerator();
	}

	public interface IEnumerator
	{
		object Current { get; }

		bool MoveNext();

		void Reset();
	}
}

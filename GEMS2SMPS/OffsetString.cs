namespace GEMS2SMPS {
	internal class OffsetString {
		public string data;
		public uint off;
		public uint len;

		public OffsetString(uint o, uint l, string d) {
			off = o;
			len = l;
			data = d;
		}
	}
}
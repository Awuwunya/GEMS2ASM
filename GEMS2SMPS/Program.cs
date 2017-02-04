using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using System.Windows.Forms;

namespace GEMS2SMPS {
	static class Program {
		#region GLOBAL VARS
		private static readonly Mutex mutex = new Mutex(true, Assembly.GetExecutingAssembly().GetName().CodeBase);
		private static bool _userRequestExit = false;
		private static bool _doIStop = false;
		static HandlerRoutine consoleHandler;
		public const char local = '.'; 
		#endregion

		[DllImport("Kernel32")]
		public static extern bool SetConsoleCtrlHandler(HandlerRoutine Handler, bool Add);

		// A delegate type to be used as the handler routine for SetConsoleCtrlHandler.
		public delegate bool HandlerRoutine(CtrlTypes CtrlType);

		// An enumerated type for the control messages sent to the handler routine.
		public enum CtrlTypes {
			CTRL_C_EVENT = 0,
			CTRL_BREAK_EVENT,
			CTRL_CLOSE_EVENT,
			CTRL_LOGOFF_EVENT = 5,
			CTRL_SHUTDOWN_EVENT
		}

		private static bool ConsoleCtrlCheck(CtrlTypes ctrlType) {
			switch(ctrlType) {
				case CtrlTypes.CTRL_C_EVENT:
					_userRequestExit = false;
					ctrlc = true;
					break;

				case CtrlTypes.CTRL_BREAK_EVENT:
				case CtrlTypes.CTRL_CLOSE_EVENT:
				case CtrlTypes.CTRL_LOGOFF_EVENT:
				case CtrlTypes.CTRL_SHUTDOWN_EVENT:
					_userRequestExit = true;
					break;
			}

			return true;
		}

		// bunch of functions used in the interface
		private static string t_get(string s, int start, int end) {
			return s.Substring(start, end - start);
		}

		private static string t_rmv(string s, int start, int end) {
			return s.Substring(0, start) + s.Substring(end, s.Length - end);
		}

		private static string t_put(string s, int off, string ch) {
			return s.Substring(0, off) + ch + s.Substring(off, s.Length - off);
		}

		private static void t_cct(string s, int start, int end, ConsoleColor fg, ConsoleColor bg) {
			Console.ForegroundColor = fg;
			Console.BackgroundColor = bg;
			Console.Write(t_get(s, start, end));
		}

		private static void t_cct(string s, int x, int y) {
			Console.SetCursorPosition(x, y);
			Console.Write(s);
		}

		// used to find what files we need to look for, what are their types, and what file extensions are associated
		private static string[][] files = {
			new string[] { "config",		"",		"GEMS" },
			new string[] { "patch bank",	"pat",	"pbank", ".*1 Instruments" },
			new string[] { "envelope bank", "env",	"mbank", ".*?2 Unknown", ".*2 Envelopes" },
			new string[] { "sample bank",	"dac",	"dbank", ".*4 Samples" },
			new string[] { "sequence bank", "seq",	"sbank", ".*3 Sequences" },
		};

		// and these are special functions for each filetype
		private static Func<byte[], string, string, string, bool>[] filesptr = {
			ParseConfig,
			ParsePatch,
			ParseEnv,
			ParseDAC,
			ParseSequence,
		};

		public static List<OffsetString> lable, code;
		public static Stopwatch timer;
		public static bool[] skippedBytes;
		public static bool ctrlc = false, pause = true, debug = false;
		public static string defl = "", folder;
		public static uint boff;
		public static long total;

		[STAThread]
		static void Main(string[] args) {
			consoleHandler = new HandlerRoutine(ConsoleCtrlCheck);
			SetConsoleCtrlHandler(consoleHandler, true);
			Console.Title = "GEMS2ASM/NAT  Built: " + new FileInfo(Assembly.GetExecutingAssembly().Location).LastWriteTime.ToShortDateString() + " " + new FileInfo(Assembly.GetExecutingAssembly().Location).LastWriteTime.ToShortTimeString();

			// various things for the interface
			int index = 0, cpos = 0, cpose = 0;
			string sp, message = "Controls:\n  ESC - Quit program\n  Enter - Confirm input and continue program\n  F1 - Change whether program pauses when complete\n  F2 - Change whether to write debug info";

			if(args != null && args.Length == 1 && !string.IsNullOrWhiteSpace(args[0])) goto ok;
			// no commandline arguments, get arguments from user
			args = new string[] { "" };

		fail:
			// prepare a string with width of the buffer - 32...
			// probably not too good method for this. Oh well.
			sp = "";
			for(int i = 32;i < Console.BufferWidth;i++)
				sp += ' ';

		message:
			Console.Clear();
			Console.ForegroundColor = ConsoleColor.Gray;
			Console.BackgroundColor = ConsoleColor.Black;

			// prepare the interface
			t_cct("Sound data folder: ", 0, 0);
			t_cct("Pause: " + (pause ? "Yes" : "No"), 0, 1);
			t_cct("Debug: " + (debug ? "Yes" : "No"), 0, 2);
			t_cct(message, 0, 2);

			// prepare drawing texts
			Console.ForegroundColor = ConsoleColor.White;
			Console.BackgroundColor = ConsoleColor.Black;
			t_cct(args[0], 19, 0);
			Console.SetCursorPosition(19 + cpos, index);
			goto repaint;

		loop:
			// wait for a key
			while(!Console.KeyAvailable) {
				Thread.Sleep(10);

				// hack: CTRL+C handler
				if(ctrlc) {
					ctrlc = false;
					Clipboard.SetText(t_get(args[index], Math.Min(cpos, cpose), Math.Max(cpos, cpose)));
				}
			}

			// key handler
			ConsoleKeyInfo c = Console.ReadKey(true);
			switch(c.Key) {
				case ConsoleKey.Escape:
					return;     // exit prg

				case ConsoleKey.Enter:
					goto end;   // accept vars

				case ConsoleKey.F1:
					pause ^= true;
					goto message;   // pause (no/yes)

				case ConsoleKey.F2:
					debug ^= true;
					goto message;   // debug (no/yes)

				case ConsoleKey.UpArrow:
					Console.SetCursorPosition(19, 0 + index);
					Console.Write(args[index]);

				//	index--;
				//	if(index < 0)
				//		index = 2;

					cpos = args[index].Length;
					cpose = cpos;
					goto repaint;   // go up 1 line

				case ConsoleKey.DownArrow:
					Console.SetCursorPosition(19, 0 + index);
					Console.Write(args[index]);

				//	index++;
				//	index %= 3;

					cpos = args[index].Length;
					cpose = cpos;
					goto repaint;   // go down 1 line

				case ConsoleKey.LeftArrow:
					if(cpos > 0) {
						cpos--;
						if((c.Modifiers & ConsoleModifiers.Shift) == 0) {
							cpose = cpos;
						}
					}
					goto repaint;   // go up 1 line

				case ConsoleKey.RightArrow:
					if(cpos < args[index].Length) {
						cpos++;
						if((c.Modifiers & ConsoleModifiers.Shift) == 0) {
							cpose = cpos;
						}
					}
					goto repaint;   // go up 1 line

				case ConsoleKey.Delete:
					args[index] = "";
					cpos = 0;
					cpose = 0;
					goto repaint;   // del all text

				case ConsoleKey.Backspace:
					if(cpos != cpose) {
						args[index] = t_rmv(args[index], Math.Min(cpos, cpose), Math.Max(cpos, cpose));
					} else if(cpos > 0) {   // remove char
						args[index] = t_rmv(args[index], cpos - 1, cpos);
						//	args[index] = args[index].Substring(0, cpos - 1) + args[index].Substring(cpos, args[index].Length - cpos);
						cpos--;
					}

					if(cpos > args[index].Length)
						cpos = args[index].Length;

					cpose = cpos;
					goto repaint;

				default:
					// handle misc keys
					if((c.Modifiers & ConsoleModifiers.Control) != 0) {
						if(c.Key == ConsoleKey.V) {
							// CTRL+V
							if(Clipboard.ContainsText()) {
								if(cpos != cpose) {
									args[index] = t_rmv(args[index], Math.Min(cpos, cpose), Math.Max(cpos, cpose));

									if(cpos > args[index].Length)
										cpos = args[index].Length;
								}

								string t = Clipboard.GetText(TextDataFormat.Text);
								args[index] = t_put(args[index], cpos, t);
								cpos += t.Length;
								cpose = cpos;
							}
							goto repaint;

						} else if(c.Key == ConsoleKey.X) {
							// CTRL+X
							if(cpos != cpose) {
								Clipboard.SetText(t_get(args[index], Math.Min(cpos, cpose), Math.Max(cpos, cpose)));
								args[index] = t_rmv(args[index], Math.Min(cpos, cpose), Math.Max(cpos, cpose));

								if(cpos > args[index].Length)
									cpos = args[index].Length;

								cpose = cpos;
								goto repaint;
							}
							goto loop;

						} else if(c.Key == ConsoleKey.A) {
							// CTRL+A
							cpose = 0;
							cpos = args[index].Length;
							goto loop;
						}
					}

					// other keys, typed an actual character (hopefully!!!)
					if(cpos != cpose) {
						args[index] = t_rmv(args[index], Math.Min(cpos, cpose), Math.Max(cpos, cpose));

						if(cpos > args[index].Length)
							cpos = args[index].Length;
					}

					args[index] = t_put(args[index], cpos, "" + c.KeyChar);
					cpos++;
					cpose = cpos;
					goto repaint;
			}

		repaint:
			// redraw the "scene"
			Console.SetCursorPosition(19, 0 + index);
			Console.Write(sp);
			Console.SetCursorPosition(19, 0 + index);
			t_cct(args[index], 0, Math.Min(cpos, cpose), ConsoleColor.White, ConsoleColor.Black);
			t_cct(args[index], Math.Min(cpos, cpose), Math.Max(cpos, cpose), ConsoleColor.Black, ConsoleColor.White);
			t_cct(args[index], Math.Max(cpos, cpose), args[index].Length, ConsoleColor.White, ConsoleColor.Black);
			Console.SetCursorPosition(Math.Min(19 + cpos, Console.BufferWidth - 1), 0 + index);
			goto loop;

		end:
			// prepare a string of full buffer width
			sp = "";
			for(int i = 1;i < Console.BufferWidth;i++)
				sp += ' ';

			// clear all of the lines and prepare for normal writing
			Console.ForegroundColor = ConsoleColor.Gray;
			Console.BackgroundColor = ConsoleColor.Black;
			Console.SetCursorPosition(0, 2);

			Console.WriteLine(sp);
			Console.WriteLine(sp);
			Console.WriteLine(sp);
			Console.WriteLine(sp);
			Console.WriteLine(sp);
			Console.WriteLine(sp);
			Console.SetCursorPosition(0, 2);

		ok:
			// look for folder
			if(Directory.Exists("..\\GEMS\\"+ args[0])) {
				folder = "..\\GEMS\\" + args[0] + "\\";

			} else if(Directory.Exists(args[0])) {
				folder = args[0] + "\\";

			} else {
				message = "Folder '" + args[0] + "' could not be found!";
				goto fail;
			}

			// delete log
			if(File.Exists(folder + ".log")) {
				File.Delete(folder + ".log");
			}

			// get a list of all the files
			List<string[]> fns = new List<string[]>();
			{
				string db = "--- List of found files:";
				foreach(string f in Directory.GetFiles(folder)) {
					string z, e = t_get(f, f.LastIndexOf('.'), f.Length);

					// we want to ignore a few files here to avoid having disassembling files that we really shouldnt
					if(e != ".asm" && e != ".id0" && e != ".id1" && e != ".nam" && e != ".til") {
						if(f.Contains('.')) z = t_get(f, 0, f.LastIndexOf('.')).Replace(folder, "");
						else z = f.Replace(folder, "");

						db += ' ' + z;
						fns.Add(new string[] { z, e });
					}
				}

				d(db +" ---");
			}

			// search for relevant files
			timer = null;
			total = 0;
			for(int i = 0;i < files.Length;i++) {
				foreach(string[] f in fns)
					foreach(string r in files[i].Skip(2)) {
						Regex a = new Regex(r);

						// check if file matches with regex supplied, and then run all the code and time it
						if(a.IsMatch(f[0])) {
							timer = new Stopwatch();
							timer.Start();

							dt("Parse " + folder + files[i][2] + f[1] + ", type is " + files[i][0]);
							Console.WriteLine("a " + files[i][0] + " will now be parsed at '" + folder + r + f[1] + "'!");
							filesptr[i](File.ReadAllBytes(folder + f[0] + f[1]), folder, files[i][2] + ".asm", files[i][1]);
						}
					}
			}

			// finish disassembling and write debug output
			Console.WriteLine("All done! Disassembled in ~"+ total + "ms!");
			if(pause) Console.ReadKey();
			wd();
		}

		// To be done
		private static bool ParseConfig(byte[] dat, string fol, string nam, string fil) {
			return true;
		}

		// parse envelopes
		private static bool ParseEnv(byte[] dat, string fol, string nam, string fil) {
			if(!Directory.Exists(folder + fil)) {
				Directory.CreateDirectory(folder + fil);
			}

			return ParseGeneric(dat, fol + nam, fil, WriteFileSeg, GenericFileName);
		}

		// parse patches
		private static bool ParsePatch(byte[] dat, string fol, string nam, string fil) {
			if(!Directory.Exists(folder + fil)) {
				Directory.CreateDirectory(folder + fil);
			}

			return ParseGeneric(dat, fol + nam, fil, ParsePatch, PatchFileName);
		}

		// patch type enum
		private enum PatType {
			FM, DAC, PSG, Noise
		}

		// patch filename function
		private static string PatchFileName(string pat, int id, byte[] data) {
			string ext = ".UNK";
			if(data.Length > 0) {
				switch(data[0]) {
					case (int)PatType.FM:
						ext = ".FM";
						break;

					case (int)PatType.DAC:
						ext = ".DAC";
						break;

					case (int)PatType.PSG:
						ext = ".PSG";
						break;

					case (int)PatType.Noise:
						ext = ".NOISE";
						break;
				}
			} else {
				ext = "";
			}

			return pat + '\\' + toHexString(id, 2) + ext;
		}

		// parse a single patch
		private static void ParsePatch(byte[] data, string filename, uint off) {
			string type;

			switch(data[0]) {
				case (int)PatType.DAC:
					AddLine("db " + "gPatDAC", off, 1);
					AddLine("db " + hex(data[1], 2), off + 1, 1);
					return;

				case (int)PatType.FM:
					type = "gPatFM";
					break;

				case (int)PatType.PSG:
					type = "gPatPSG";
					break;

				case (int)PatType.Noise:
					type = "gPatNoise";
					break;

				default:
					type = hex(data[0], 2);
					break;
			}

			AddLine("\tdc.b " + type, off, 1);
			AddLine("\tgincbin " + filename, off + 1, 0);
			CreateFile(folder + filename, data, 1, data.Length);
		}

		// parse a DAC file
		private static bool ParseDAC(byte[] dat, string fol, string nam, string fil) {
			if(!Directory.Exists(folder + fil)) {
				Directory.CreateDirectory(folder + fil);
			}

			// skip all bytes
			skippedBytes = new bool[dat.Length];
			for(int i = 0;i < skippedBytes.Length;i++) {
				skippedBytes[i] = true;
			}

			lable = new List<OffsetString>();
			code = new List<OffsetString>();
			AddLine("\tgSetOffset", 0, 0);

			uint last = 0xFFFFFFFF, num = 0;
			boff = 0;
			while(boff < last) {
				// find the first offset
				uint o = offset3(dat, boff + 1);
				if(o < last & o > boff) {
					last = o;
				}

				dx("Found ptr to "+ hex(o, 4) +", named "+ local + num);
				AddLable(""+ local + toHexString(num, 2), o);
				
				// get sample definition
				string q = "\tgSample "+ local + toHexString(num++, 2) + ", ";
				for(int i = 0;i < 8;i+=2) {
					q += hex(0xFFFF & word(dat, boff + 4 + (uint)i), 4) + ", ";
				}

				byte b = dat[boff];
				if((b & 0x10) != 0) q += "gdLoop|";
				if((b & 0x20) != 0) q += "gdNoteStop|";

				AddLine(q + hex(b & 0xF, 2), boff, 12);
			}

			// processs the rest of the file and write it out
			GetSegments(WriteFileSeg, GenericFileName, dat, lable.ToArray(), fil);
			return PrintFile(fol + nam, dat);
		}

		// function for simplifying many functions
		private static bool ParseGeneric(byte[] dat, string nam, string fil, Action<byte[], string, uint> process, Func<string, int, byte[], string> argString) {
			// skip all bytes
			skippedBytes = new bool[dat.Length];
			for(int i = 0;i < skippedBytes.Length;i++) {
				skippedBytes[i] = true;
			}

			lable = new List<OffsetString>();
			code = new List<OffsetString>();
			AddLine("\tgSetOffset", 0, 0);

			uint last = 0xFFFFFFFF, num = 0;
			boff = 0;
			while(boff < last) {
				// find the first offset
				uint o = offset(dat, boff);
				if(o < last & o > boff) {
					last = o;
				}

				// define this offset
				dx("Found ptr to " + hex(o, 4) + ", named " + local + num);
				AddLable("" + local + toHexString(num, 2), o);
				AddLine("\tgOffset " + local + toHexString(num++, 2), boff, 2);
			}

			// processs the rest of the file and write it out
			GetSegments(process, argString, dat, lable.ToArray(), fil);
			return PrintFile(nam, dat);
		}

		// splits files defined by lables passed to here, and gives these files to a special handler
		private static void GetSegments(Action<byte[], string, uint> process, Func<string, int, byte[], string> argString, byte[] data, OffsetString[] lables, string strBase) {
			uint start = 0;
			int num = -1;
			byte[] d;

			// sort lables and process them
			int[] sortlist = new int[lables.Length];
			foreach(OffsetString o in sort(lables, sortlist)) {
				if(start == 0) {
					// usually the first lable, but sometimes previous lable pointed to 0 too
					start = o.off;
					num++;

				} else {
					d = Array(data, (int)start, (int)(o.off - start));
					process(d, argString(strBase, sortlist[num++], d), start);
					start = o.off;
				}
			}
			
			// do the final segment
			d = Array(data, (int)start, (int)(data.Length - start));
			process(d, argString(strBase, sortlist[num++], d), start);
		}

		// generic function to write a generic file
		private static void WriteFileSeg(byte[] data, string filename, uint off) {
			AddLine("\tgincbin " + filename, off + 1, 0);
			CreateFile(folder + filename, data, 0, data.Length);
		}

		// generic function to create a generic filename
		private static string GenericFileName(string pat, int id, byte[] data) {
			return pat +'\\'+ toHexString(id, 2) + ".dat";
		}

		// list of available notes
		private enum Notes {
			nRst = 0, nC0, nCs0, nD0, nEb0, nE0, nF0, nFs0, nG0, nAb0, nA0, nBb0, nB0, nC1, nCs1, nD1,
			nEb1, nE1, nF1, nFs1, nG1, nAb1, nA1, nBb1, nB1, nC2, nCs2, nD2, nEb2, nE2, nF2, nFs2,
			nG2, nAb2, nA2, nBb2, nB2, nC3, nCs3, nD3, nEb3, nE3, nF3, nFs3, nG3, nAb3, nA3, nBb3,
			nB3, nC4, nCs4, nD4, nEb4, nE4, nF4, nFs4, nG4, nAb4, nA4, nBb4, nB4, nC5, nCs5, nD5,
			nEb5, nE5, nF5, nFs5, nG5, nAb5, nA5, nBb5, nB5, nC6, nCs6, nD6, nEb6, nE6, nF6, nFs6,
			nG6, nAb6, nA6, nBb6, nB6, nC7, nCs7, nD7, nEb7, nE7, nF7, nFs7, nG7, nAb7, nA7, nBb7,
		}

		// list of conditions in command seqif
		private enum Conditions {
			eq = 0, ne, gt, ge, lt, le, 
		}

		private static int call, ifcc;

		// parse a sequence
		private static bool ParseSequence(byte[] dat, string fol, string nam, string fil) {
			skippedBytes = new bool[dat.Length];
			lable = new List<OffsetString>();
			code = new List<OffsetString>();
			AddLine("\tgSetOffset", 0, 0);

			uint last = 0xFFFFFFFF, num = 0;
			boff = 0;
			while(boff < last) {
				// find the first offset
				uint o = offset(dat, boff);
				if(o < last & o > boff) {
					last = o;
				}

				// define this offset
				dx("Found ptr to " + hex(o, 4) + ", named " + local + num);
				AddLable("" + local + toHexString(num, 2), o);
				AddLine("\tgOffset " + local + toHexString(num++, 2), boff, 2);
			}

			// sort the lables
			OffsetString[] off = sort(lable.ToArray(), new int[lable.Count]);
			
			// find out whether we are using 2 or 3-byte headers
			bool off3 = false;
			for(int i = 0;i < off.Length - 1;i ++) {
				int c = dat[off[i].off];

				if(c > 1) {
					uint len = off[i + 1].off - off[i].off - 1;
					if(len / c == 2) {
						off3 = false;
						break;

					} else if(len / c == 3) {
						off3 = true;
						break;
					}
				}
			}

			// parse sequence headers
			foreach(OffsetString o in off) {
				AddLine("db "+ hex(dat[o.off], 2), o.off, 1);

				for(int i = 0;i < dat[o.off];i++) {
						if(off3) {
							AddLable(o.data + "_" + toHexString(i, 2), offset3(dat, (uint)(o.off + (i * 3)) + 1));
							AddLine("\tgChannel " + o.data + "_" + toHexString(i, 2), (uint)(o.off + (i * 3)) + 1, 3);

						} else {
							AddLable(o.data + "_" + toHexString(i, 2), offset(dat, (uint)(o.off + (i * 2)) + 1));
							AddLine("\tgChannel " + o.data +"_"+ toHexString(i, 2), (uint)(o.off + (i * 2)) + 1, 2);
						}
				}
			}

			// parse channel data
			call = 0;
			ifcc = 0;
			foreach(OffsetString o in lable.ToArray()) {
				if(o.data.Contains("_")) {
					boff = o.off;
					ParseSeqAt(dat);
				}
			}

			// print out the file
			return PrintFile(fol + nam, dat);
		}

		private static void ParseSeqAt(byte[] dat) {
			bool stop = false, infi = false;
			// loop until stop token
			while(!stop) {
				// or we run out of the file
				if(boff >= dat.Length)
					break;

				if(dat[boff] < 0x60) {
					// a note (0x00-0x60)
					AddLine("db " + ((Notes)dat[boff]).ToString(), boff, 1);

				} else if(dat[boff] < 0x80) {
					// a command (0x60-0x80)
					switch(dat[boff]) {
						case 96:    // seqeos
							stop = true;
							AddLine("\tgStop", boff, 1);
							break;

						case 97:    // seqpchange
							AddLine("\tgsPatch\t\t" + hex(dat[boff + 1], 2), boff, 2);
							break;

						case 98:    // seqenv
							AddLine("\tgsEnv\t\t" + hex(dat[boff + 1], 2), boff, 2);
							break;

						case 99:    // seqdelay
							AddLine("\tgNop", boff, 1);
							break;

						case 100: {    // seqsloop
								byte b = dat[boff + 1];
								if(b >= 0x7F) {
									AddLine("\tgLoopStart", boff, 2);
									infi = true;

								} else {
									AddLine("\tgLoopStart\t" + hex(b, 2), boff, 2);
								}
								break;
							}

						case 101:    // seqeloop
							AddLine("\tgLoopEnd", boff, 1);
							stop = infi;	// in case of loopstart 0x7F, we also want to ignroe anything that comes after
							break;

						case 102:    // seqretrig
							AddLine("\tgRetrig", boff, 1);
							break;

						case 103:    // seqsus
							AddLine("\tgHold", boff, 1);
							break;

						case 104:    // seqtempo
							AddLine("\tgsTempo\t\t" + ((dat[boff + 1] + 40) & 0xFF), boff, 2);
							break;

						case 105: {    // seqmute
								byte b = dat[boff + 1];
								AddLine("\tg" + ((b & 0x10) == 0 ? "Unm" : "M") + "ute\t" + (b & 0xF), boff, 2);
								break;
							}

						case 106:    // seqprio
							AddLine("\tgsPrio\t\t" + hex(dat[boff + 1], 2), boff, 2);
							break;

						case 107:    // seqssong
							AddLine("\tgStartSeq\t" + hex(dat[boff + 1], 2), boff, 2);
							break;

						case 108:    // seqpbend
							AddLine("\tgPitBend\t" + hex(0xFFFF & word(dat, boff + 1), 4), boff, 3);
							break;

						case 109:    // seqsfx
							AddLine("\tgSFX", boff, 1);
							break;

						case 110:    // seqsamprate
							AddLine("\tgsSampleRate\t" + hex(dat[boff + 1], 2), boff, 2);
							break;

						case 111: {    // seqgoto
								uint xof = (uint)((boff + 1) + word(dat, boff + 1));
								AddLable(".call_" + toHexString(call, 2), xof);
								AddLine("\tgCall\t\t.call_" + toHexString(call++, 2), boff, 3);

								// parse at that location and then resume operation
								uint xboff = boff;
								boff = xof;
								ParseSeqAt(dat);
								boff = xboff;
								break;
							}

						case 112:    // seqstore
							AddLine("\tgStore\t\t" + hex(dat[boff + 1], 2) +", "+ hex(dat[boff + 1], 2), boff, 3);
							break;

						case 113: {    // seqif
								uint xof = (boff + 4) + (uint)(dat[boff + 4] & 0xFF);
								AddLable(".if_" + toHexString(ifcc, 2), xof);
								AddLine("\tgIf"+ ((Conditions)dat[boff + 2]).ToString() + "\t\t" + hex(dat[boff + 1], 2) + ", " + hex(dat[boff + 3], 2) + ".if_" + toHexString(ifcc++, 2), boff, 3);

								// parse at that location and then resume operation
								uint xboff = boff;
								boff = xof;
								ParseSeqAt(dat);
								boff = xboff;
								break;
							}

						case 114: {    // seqseekrit
								skippedBytes[boff] = true;
								boff++;
								byte val = dat[boff + 1];
								switch(dat[boff]) {
									case 0:     // seqstopseq
										AddLine("\tgStopSeq\t" + hex(val, 2), boff, 2);
										break;

									case 1:     // seqpauseseq
										AddLine("\tgPauseSeq\t" + hex(val, 2), boff, 2);
										break;

									case 2:     // seqresume
										AddLine("\tgUnpauseSeq\t" + hex(val, 2), boff, 2);
										break;

									case 3:     // seqpauselmusic
										AddLine("\tgPauseSeq2", boff, 1);
										break;

									case 4:     // seqatten
										AddLine("\tgsVolumeAll\t" + hex(val, 2), boff, 2);
										break;

									case 5:     // seqchatten
										AddLine("\tgsVolume\t" + hex(val, 2), boff, 2);
										break;

									default:
										AddLine("db " + hex(114, 2), boff-1, 0);
										AddLine("db " + hex(dat[boff], 2), boff, 1);
										break;
								}
							}
							break;

						default:
							AddLine("db " + hex(dat[boff], 2), boff, 1);
							break;
					}

				} else {
					// other
					AddLine("db " + hex(dat[boff], 2), boff, 1);
				}
			}
		}

		// writes bytes to a file
		private static void CreateFile(string f, byte[] dat, int start, int end) {
			using(BinaryWriter r = new BinaryWriter(new FileStream(f, FileMode.Create, FileAccess.Write))) {
				r.Write(dat, start, end - start);
				r.Flush();
				r.Dispose();
			}
		}

		// sort list of OffsetString's
		private static OffsetString[] sort(OffsetString[] list, int[] order) {
			if(list == null || order == null || list.Length != order.Length) {
				throw new Exception("IllegalStateException: list and order size are not same!");
			}

			List<OffsetString> sorted = new List<OffsetString>();
			foreach(OffsetString o in list) {
				int i = 0;
				bool e = false;

				// look for a place to put this entry in
				foreach(OffsetString a in sorted) {
					if(a.off >= o.off) {
						sorted.Insert(i, o);
						e = true;
						break;
					}

					++i;
				}

				// if no place was found, just put it somewhere
				if(!e) sorted.Add(o);
			}

			// fill the order list to see what was relocated where
			for(int i = 0;i < order.Length;i++) {
				int a = 0;
				foreach(OffsetString o in list) {
					if(o == sorted[i]) {
						order[i] = a;
						break;
					}

					++a;
				}
			}

			return sorted.ToArray();
		}

		// print file into disk
		private static bool PrintFile(string f, byte[] dat) {
			timer.Stop();
			total += timer.ElapsedMilliseconds;
			Console.WriteLine("Parsing took " + timer.ElapsedMilliseconds + "ms!");
			timer = new Stopwatch();
			timer.Start();

			try {
				using(TextWriter r = File.CreateText(f)) {
					uint currLineArg = 0, nxtchk = 0;
					bool lastwaslable = true;
					string currLine = "";

					for(int b = -1;b < dat.Length;b++) {
						bool linechanged = false;
						OffsetString ln = new OffsetString(uint.MaxValue, 1, ">>>>>>>>>>>>>>>");

						foreach(OffsetString o in code) {
							// check if this line should go here
							if((int)o.off == b && !ln.data.Equals(o.data)) {
								// checks if there is another line found already. Warns the user if so
								if(ln.len > 0 && (ulong)b == ln.off) {
									Console.WriteLine("Warning: Could not decide line at " + hex(b, 4) + "! '" + ln.data + "' vs '" + o.data + "'");
									dt("Conflict at " + hex(b, 4) + " '" + ln.data + "' vs '" + o.data + "'");
								}

								ln = o;
								linechanged = true;
								// check if this is data
								if(o.data.StartsWith("db ")) {
									// if there were not already data line started, start a new one
									if(currLineArg == 0) {
										currLine = "\tdc.b ";

										// if there was 8 or more bytes in this line, start new line
									} else if(currLineArg >= 8) {
										r.Write(currLine.Substring(0, currLine.Length - 2) + '\n');
										currLineArg = 0;
										currLine = "\tdc.b ";
									}

									// then finally add the byte to the line
									currLine += o.data.Substring(3) + ", ";
									currLineArg++;

								} else {
									// split byte line if there was one
									if(currLineArg > 0) {
										r.Write(currLine.Substring(0, currLine.Length - 2) + '\n');
										currLineArg = 0;
									}

									// add the line to file
									r.Write(o.data + '\n');
									lastwaslable = false;
									dt(o.data);
								}
							}
						}

						// checks if no line was found for this byte,
						// last line did not have its bytes extend here,
						// and this byte was not in skipped bytes list
						if(!linechanged && b >= nxtchk && !skippedBytes[b]) {
							if(currLineArg == 0) {
								// if no bytes exist yet, note this as unused bytes and start line
								currLine = "\t; Unused\n\tdc.b ";

							} else if(currLineArg >= 8) {
								// if there are 8 bytes in line, split the line and start new one
								r.Write(currLine.Substring(0, currLine.Length - 2) + '\n');
								currLineArg = 0;
								currLine = "\tdc.b ";
							}

							// add a byte to the line
							string z = hex(dat[b], 2);
							dt("Unused at " + hex(b, 4) + ", arg# " + currLineArg + "', data " + z);
							currLine += z + ", ";
							currLineArg++;
						}

						// check if any lables are placed at the offset
						foreach(OffsetString o in lable) {
							if((int)o.off == b + 1) {
								// split bytes of there are any.
								if(currLineArg > 0) {
									r.Write(currLine.Substring(0, currLine.Length - 2) + '\n');
									currLineArg = 0;
								}

								// checks if last line also was a lable. Adds extra line break if not
								if(lastwaslable) {
									r.Write((o.data.StartsWith(".") && o.data.Length < 8 ? "\n" : "") + o.data + (o.data.StartsWith(".") && o.data.Length < 8 ? "" : "\n"));

								} else {
									r.Write('\n' + o.data + (o.data.StartsWith(".") && o.data.Length < 8 ? "" : "\n"));
									lastwaslable = true;
								}
							}
						}

						// if line was found, do not check for unused bytes until the line's bytecount is up
						if(linechanged && ln.len > 0) {
							nxtchk = ((uint)b + ln.len);
						}
					}

					// split bytes that were left
					if(currLineArg > 0) {
						r.Write(currLine.Substring(0, currLine.Length - 2) + '\n');
					}
				}

			} catch(Exception e) {
				error("Failed to write file '" + f + "'! ", e);
				return false;
			}

			timer.Stop();
			total += timer.ElapsedMilliseconds;
			Console.WriteLine("Writing took " + timer.ElapsedMilliseconds + "ms!");
			return true;
		}

		// function to create a subarray
		public static T[] Array<T>(this T[] data, int index, int length) {
			if(index + length > data.Length) return new T[0];

			T[] result = new T[length];
			System.Array.Copy(data, index, result, 0, length);
			return result;
		}

		// get GEMS offset (0 + z80word)
		private static uint offset(byte[] dat, uint off) {
			return (uint)((word(dat, off)) & 0xFFFF);
		}

		// get GEMS offset (0 + 3 byte offset)
		private static uint offset3(byte[] dat, uint off) {
			return (uint)((tribyte(dat, off)) & 0xFFFFFF);
		}

		// get z80 word (16-bit)
		private static short word(byte[] dat, uint off) {
			return (short)((dat[off + 1] << 8) | dat[off]);
		}

		// get GEMS z80 tribyte (24-bit)
		private static int tribyte(byte[] dat, uint off) {
			return (dat[off + 2] << 16) | (dat[off + 1] << 8) | dat[off];
		}

		private static void AddLable(string l, uint off) {
			lable.Add(new OffsetString(off, 0, l));
		}

		private static void AddLine(string l, uint off, uint len) {
			code.Add(new OffsetString(off, len, l));
			boff += len;
		}

		private static string hex(long res, int zeroes) {
			return "$" + toHexString(res, zeroes);
		}

		private static string toHexString(long res, int zeroes) {
			return string.Format("{0:x" + zeroes + "}", res).ToUpper();
		}

		private static string toBinaryString(long res, int zeroes) {
			return "%" + Convert.ToString(res, 2).PadLeft(zeroes);
		}

		// print error info, and then exits the program after user input is received
		public static void error(string str) {
			wd();
			Console.Write(str);
			Console.ReadKey(true);
			Environment.Exit(-1);
		}

		public static void error(string str, Exception e) {
			wd();
			Console.Write(str);
			Console.WriteLine(e.ToString());
			Console.ReadKey(true);
			Environment.Exit(-1);
		}

		// prints debug INFO level text
		private static void dt(string v) {
			if(debug) d("--- " + v + " ---");
		}

		// prints debug NORMAL level text
		private static void d(string v) {
			if(debug) defl += v + "\r\n";
		}

		// prints debug NORMAL level text with hex offset
		private static void dx(string v, int o) {
			if(debug) d(hex(o, 4) + " " + v);
		}

		private static void dx(string v) {
			if(debug) d(hex((int)boff, 4) + " " + v);
		}

		// write debug file
		private static void wd() {
			if(debug) File.WriteAllText(folder + ".log", defl);
		}
	}
}
 
#region Using declarations
using System;
using System.ComponentModel;
using System.ComponentModel.DataAnnotations;
using System.Windows.Media;
using System.Xml.Serialization;
using NinjaTrader.Data;
using NinjaTrader.Gui;
using NinjaTrader.Gui.Chart;
using NinjaTrader.NinjaScript;
using NinjaTrader.NinjaScript.DrawingTools;
#endregion

// GuIndicator: round levels + previous session close + previous day/week/month high/low.
// Everything is drawn in OnRender (no Draw objects), visible price range only.
namespace NinjaTrader.NinjaScript.Indicators
{
	public class GuIndicator : Indicator
	{
		private double prevSessionClose = double.NaN;

		// Previous period high/low (NaN = not available)
		private double prevDayHi = double.NaN, prevDayLo = double.NaN;
		private double prevWeekHi = double.NaN, prevWeekLo = double.NaN;
		private double prevMonthHi = double.NaN, prevMonthLo = double.NaN;
		// Current period
		private double curDayHi = double.NaN, curDayLo = double.NaN;
		private double curWeekHi = double.NaN, curWeekLo = double.NaN;
		private double curMonthHi = double.NaN, curMonthLo = double.NaN;
		// A period is published as "previous" only if it was seen complete from its start
		private bool dayFull, weekFull, monthFull;
		private DateTime curWeekKey = DateTime.MinValue;
		private int curMonthKey = -1;
		private SessionIterator sessIt;
		// Non-intraday charts (day/week/month/year): each bar is a complete period.
		// barRank: 0 = intraday or 1 day, 1 = 1 week, 2 = 1 month, 3 = other (no previous high/low)
		private bool nonIntraday;
		private int barRank;

		private SharpDX.Direct2D1.Brush       dxLevelBrush;
		private SharpDX.Direct2D1.Brush       dxMajorBrush;
		private SharpDX.Direct2D1.Brush       dxCloseBrush;
		private SharpDX.Direct2D1.Brush       dxDayBrush;
		private SharpDX.Direct2D1.Brush       dxWeekBrush;
		private SharpDX.Direct2D1.Brush       dxMonthBrush;
		private SharpDX.Direct2D1.StrokeStyle dxDash;
		private SharpDX.Direct2D1.StrokeStyle dxDashDot;

		protected override void OnStateChange()
		{
			if (State == State.SetDefaults)
			{
				Name                     = "GuIndicator";
				Description              = "Round levels ending in 00 (dash) and 50 (dash-dot), previous session close and previous day/week/month high/low.";
				Calculate                = Calculate.OnEachTick;
				IsOverlay                = true;
				DisplayInDataBox         = false;
				DrawOnPricePanel         = true;
				PaintPriceMarkers        = false;
				IsAutoScale              = false;
				IsSuspendedWhileInactive = true;
				ScaleJustification       = ScaleJustification.Right;

				Step          = 50;
				MaxLines      = 200;
				ShowPrevClose = true;
				ShowLevel00   = false;
				ShowLevel50   = false;
				LevelBrush    = Brushes.Red;
				MajorBrush    = Brushes.LightSkyBlue;
				Brush orange  = new SolidColorBrush(Color.FromRgb(255, 190, 110));
				orange.Freeze();
				CloseBrush    = orange;

				ShowPrevDay   = true;
				ShowPrevWeek  = true;
				ShowPrevMonth = true;
				DayBrush      = Brushes.Silver;
				Brush lightPurple = new SolidColorBrush(Color.FromRgb(216, 180, 254));
				lightPurple.Freeze();
				WeekBrush     = lightPurple;
				Brush midPurple = new SolidColorBrush(Color.FromRgb(160, 100, 220));
				midPurple.Freeze();
				MonthBrush    = midPurple;
			}
			else if (State == State.DataLoaded)
			{
				sessIt = new SessionIterator(Bars);
				BarsPeriodType bpt = BarsPeriod.BarsPeriodType;
				int bpv = BarsPeriod.Value;
				nonIntraday = bpt == BarsPeriodType.Day || bpt == BarsPeriodType.Week || bpt == BarsPeriodType.Month || bpt == BarsPeriodType.Year;
				if (!nonIntraday)                                   barRank = 0;
				else if (bpt == BarsPeriodType.Day   && bpv == 1)   barRank = 0;
				else if (bpt == BarsPeriodType.Week  && bpv == 1)   barRank = 1;
				else if (bpt == BarsPeriodType.Month && bpv == 1)   barRank = 2;
				else                                                barRank = 3;
			}
			else if (State == State.Terminated)
			{
				DisposeDx();
			}
		}

		protected override void OnBarUpdate()
		{
			if (IsFirstTickOfBar && (nonIntraday || Bars.IsFirstBarOfSession))
				OnNewSession();

			// Update high/low of the current periods
			double h = High[0], l = Low[0];
			curDayHi   = double.IsNaN(curDayHi)   ? h : Math.Max(curDayHi, h);
			curDayLo   = double.IsNaN(curDayLo)   ? l : Math.Min(curDayLo, l);
			curWeekHi  = double.IsNaN(curWeekHi)  ? h : Math.Max(curWeekHi, h);
			curWeekLo  = double.IsNaN(curWeekLo)  ? l : Math.Min(curWeekLo, l);
			curMonthHi = double.IsNaN(curMonthHi) ? h : Math.Max(curMonthHi, h);
			curMonthLo = double.IsNaN(curMonthLo) ? l : Math.Min(curMonthLo, l);
		}

		private void OnNewSession()
		{
			if (CurrentBar >= 1)
				prevSessionClose = Close[1];

			DateTime td;
			if (nonIntraday)
				td = Time[0].Date;
			else
			{
				sessIt.GetNextSession(Time[0], true);
				td = sessIt.ActualTradingDayExchange.Date;
			}
			DateTime weekKey = td.AddDays(-(((int)td.DayOfWeek + 6) % 7)); // Monday
			int monthKey = td.Year * 12 + td.Month;
			bool first = CurrentBar == 0;

			// Day
			if (!first)
			{
				prevDayHi = dayFull ? curDayHi : double.NaN;
				prevDayLo = dayFull ? curDayLo : double.NaN;
			}
			curDayHi = curDayLo = double.NaN;
			dayFull = !first;

			// Week
			if (weekKey != curWeekKey)
			{
				if (curWeekKey != DateTime.MinValue)
				{
					prevWeekHi = weekFull ? curWeekHi : double.NaN;
					prevWeekLo = weekFull ? curWeekLo : double.NaN;
				}
				weekFull = curWeekKey != DateTime.MinValue;
				curWeekKey = weekKey;
				curWeekHi = curWeekLo = double.NaN;
			}

			// Month
			if (monthKey != curMonthKey)
			{
				if (curMonthKey != -1)
				{
					prevMonthHi = monthFull ? curMonthHi : double.NaN;
					prevMonthLo = monthFull ? curMonthLo : double.NaN;
				}
				monthFull = curMonthKey != -1;
				curMonthKey = monthKey;
				curMonthHi = curMonthLo = double.NaN;
			}
		}

		public override void OnRenderTargetChanged()
		{
			DisposeDx();
			if (RenderTarget == null)
				return;

			dxLevelBrush = LevelBrush.ToDxBrush(RenderTarget);
			dxMajorBrush = MajorBrush.ToDxBrush(RenderTarget);
			dxCloseBrush = CloseBrush.ToDxBrush(RenderTarget);
			dxDayBrush   = DayBrush.ToDxBrush(RenderTarget);
			dxWeekBrush  = WeekBrush.ToDxBrush(RenderTarget);
			dxMonthBrush = MonthBrush.ToDxBrush(RenderTarget);

			SharpDX.Direct2D1.Factory f = RenderTarget.Factory;
			dxDash    = new SharpDX.Direct2D1.StrokeStyle(f, new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.Dash });
			dxDashDot = new SharpDX.Direct2D1.StrokeStyle(f, new SharpDX.Direct2D1.StrokeStyleProperties { DashStyle = SharpDX.Direct2D1.DashStyle.DashDot });
		}

		protected override void OnRender(ChartControl chartControl, ChartScale chartScale)
		{
			// Nothing is drawn during hit-test: lines cannot be selected or deleted with Del.
			if (IsInHitTest || RenderTarget == null || dxLevelBrush == null || ChartPanel == null)
				return;

			float x0 = ChartPanel.X;
			float x1 = ChartPanel.X + ChartPanel.W;
			double min = chartScale.MinValue;
			double max = chartScale.MaxValue;

			SharpDX.Direct2D1.AntialiasMode oldAa = RenderTarget.AntialiasMode;
			RenderTarget.AntialiasMode = SharpDX.Direct2D1.AntialiasMode.Aliased;

			if ((ShowLevel00 || ShowLevel50) && Step > 0 && max > min)
			{
				long kMin = (long)Math.Ceiling(min / Step);
				long kMax = (long)Math.Floor(max / Step);

				if (kMax - kMin <= MaxLines)
				{
					// Step 50: even k -> ends in 00 (dash), odd k -> ends in 50 (dash-dot)
					for (long k = kMin; k <= kMax; k++)
						if (k % 2 == 0 ? ShowLevel00 : ShowLevel50)
							DrawLevel(chartScale, k * Step, x0, x1, k % 2 == 0 ? dxDash : dxDashDot, k % 2 == 0 ? dxMajorBrush : dxLevelBrush);
				}
				else
				{
					// Zoomed out too far: 00 levels only
					double big = Step * 2;
					long jMin = (long)Math.Ceiling(min / big);
					long jMax = (long)Math.Floor(max / big);
					if (ShowLevel00 && jMax - jMin <= MaxLines)
						for (long j = jMin; j <= jMax; j++)
							DrawLevel(chartScale, j * big, x0, x1, dxDash, dxMajorBrush);
				}
			}

			// Only draw periods that the chart's bars can compose exactly
			bool dayOk   = barRank == 0;
			bool weekOk  = barRank == 0 || barRank == 1;
			bool monthOk = barRank == 0 || barRank == 2;

			if (ShowPrevClose && dayOk && !double.IsNaN(prevSessionClose) && prevSessionClose >= min && prevSessionClose <= max)
			{
				float y = Snap(chartScale.GetYByValue(prevSessionClose));
				RenderTarget.DrawLine(new SharpDX.Vector2(x0, y), new SharpDX.Vector2(x1, y), dxCloseBrush, 1f);
			}

			if (ShowPrevMonth && monthOk) { DrawSolid(chartScale, prevMonthHi, x0, x1, dxMonthBrush); DrawSolid(chartScale, prevMonthLo, x0, x1, dxMonthBrush); }
			if (ShowPrevWeek && weekOk) { DrawSolid(chartScale, prevWeekHi,  x0, x1, dxWeekBrush);  DrawSolid(chartScale, prevWeekLo,  x0, x1, dxWeekBrush); }
			if (ShowPrevDay && dayOk) { DrawSolid(chartScale, prevDayHi,   x0, x1, dxDayBrush);   DrawSolid(chartScale, prevDayLo,   x0, x1, dxDayBrush); }

			RenderTarget.AntialiasMode = oldAa;
		}

		private void DrawSolid(ChartScale chartScale, double price, float x0, float x1, SharpDX.Direct2D1.Brush brush)
		{
			if (double.IsNaN(price) || brush == null || price < chartScale.MinValue || price > chartScale.MaxValue)
				return;
			float y = Snap(chartScale.GetYByValue(price));
			RenderTarget.DrawLine(new SharpDX.Vector2(x0, y), new SharpDX.Vector2(x1, y), brush, 1f);
		}

		private void DrawLevel(ChartScale chartScale, double price, float x0, float x1, SharpDX.Direct2D1.StrokeStyle style, SharpDX.Direct2D1.Brush brush)
		{
			float y = Snap(chartScale.GetYByValue(price));
			RenderTarget.DrawLine(new SharpDX.Vector2(x0, y), new SharpDX.Vector2(x1, y), brush, 1f, style);
		}

		private static float Snap(int y)
		{
			return y + 0.5f;
		}

		private void DisposeDx()
		{
			if (dxLevelBrush != null) { dxLevelBrush.Dispose(); dxLevelBrush = null; }
			if (dxMajorBrush != null) { dxMajorBrush.Dispose(); dxMajorBrush = null; }
			if (dxCloseBrush != null) { dxCloseBrush.Dispose(); dxCloseBrush = null; }
			if (dxDayBrush   != null) { dxDayBrush.Dispose();   dxDayBrush   = null; }
			if (dxWeekBrush  != null) { dxWeekBrush.Dispose();  dxWeekBrush  = null; }
			if (dxMonthBrush != null) { dxMonthBrush.Dispose(); dxMonthBrush = null; }
			if (dxDash       != null) { dxDash.Dispose();       dxDash       = null; }
			if (dxDashDot    != null) { dxDashDot.Dispose();    dxDashDot    = null; }
		}

		#region Properties
		[Range(0.0000001, double.MaxValue)]
		[Display(Name = "Step (points)", Description = "Distance between levels (50 = levels ending in 50 and 00). Every 2 steps = 00 level.", Order = 1, GroupName = "Levels")]
		public double Step { get; set; }

		[Range(10, 2000)]
		[Display(Name = "Max visible lines", Description = "If exceeded, only 00 levels are drawn.", Order = 2, GroupName = "Levels")]
		public int MaxLines { get; set; }

		[XmlIgnore]
		[Display(Name = "50 levels color", Order = 3, GroupName = "Levels")]
		public Brush LevelBrush { get; set; }

		[Browsable(false)]
		public string LevelBrushSerializable
		{
			get { return Serialize.BrushToString(LevelBrush); }
			set { LevelBrush = Serialize.StringToBrush(value); }
		}

		[XmlIgnore]
		[Display(Name = "00 levels color", Order = 4, GroupName = "Levels")]
		public Brush MajorBrush { get; set; }

		[Browsable(false)]
		public string MajorBrushSerializable
		{
			get { return Serialize.BrushToString(MajorBrush); }
			set { MajorBrush = Serialize.StringToBrush(value); }
		}

		[Display(Name = "Show 00 levels", Order = 5, GroupName = "Levels")]
		public bool ShowLevel00 { get; set; }

		[Display(Name = "Show 50 levels", Order = 6, GroupName = "Levels")]
		public bool ShowLevel50 { get; set; }

		[Display(Name = "Show previous session close", Order = 1, GroupName = "Session close")]
		public bool ShowPrevClose { get; set; }

		[XmlIgnore]
		[Display(Name = "Close color", Order = 2, GroupName = "Session close")]
		public Brush CloseBrush { get; set; }

		[Browsable(false)]
		public string CloseBrushSerializable
		{
			get { return Serialize.BrushToString(CloseBrush); }
			set { CloseBrush = Serialize.StringToBrush(value); }
		}

		[Display(Name = "Show previous day high/low", Order = 1, GroupName = "Previous high/low")]
		public bool ShowPrevDay { get; set; }

		[XmlIgnore]
		[Display(Name = "Previous day color", Order = 2, GroupName = "Previous high/low")]
		public Brush DayBrush { get; set; }

		[Browsable(false)]
		public string DayBrushSerializable
		{
			get { return Serialize.BrushToString(DayBrush); }
			set { DayBrush = Serialize.StringToBrush(value); }
		}

		[Display(Name = "Show previous week high/low", Order = 3, GroupName = "Previous high/low")]
		public bool ShowPrevWeek { get; set; }

		[XmlIgnore]
		[Display(Name = "Previous week color", Order = 4, GroupName = "Previous high/low")]
		public Brush WeekBrush { get; set; }

		[Browsable(false)]
		public string WeekBrushSerializable
		{
			get { return Serialize.BrushToString(WeekBrush); }
			set { WeekBrush = Serialize.StringToBrush(value); }
		}

		[Display(Name = "Show previous month high/low", Order = 5, GroupName = "Previous high/low")]
		public bool ShowPrevMonth { get; set; }

		[XmlIgnore]
		[Display(Name = "Previous month color", Order = 6, GroupName = "Previous high/low")]
		public Brush MonthBrush { get; set; }

		[Browsable(false)]
		public string MonthBrushSerializable
		{
			get { return Serialize.BrushToString(MonthBrush); }
			set { MonthBrush = Serialize.StringToBrush(value); }
		}
		#endregion
	}
}

#region NinjaScript generated code. Neither change nor remove.

namespace NinjaTrader.NinjaScript.Indicators
{
	public partial class Indicator : NinjaTrader.Gui.NinjaScript.IndicatorRenderBase
	{
		private GuIndicator[] cacheGuIndicator;
		public GuIndicator GuIndicator()
		{
			return GuIndicator(Input);
		}

		public GuIndicator GuIndicator(ISeries<double> input)
		{
			if (cacheGuIndicator != null)
				for (int idx = 0; idx < cacheGuIndicator.Length; idx++)
					if (cacheGuIndicator[idx] != null &&  cacheGuIndicator[idx].EqualsInput(input))
						return cacheGuIndicator[idx];
			return CacheIndicator<GuIndicator>(new GuIndicator(), input, ref cacheGuIndicator);
		}
	}
}

namespace NinjaTrader.NinjaScript.MarketAnalyzerColumns
{
	public partial class MarketAnalyzerColumn : MarketAnalyzerColumnBase
	{
		public Indicators.GuIndicator GuIndicator()
		{
			return indicator.GuIndicator(Input);
		}

		public Indicators.GuIndicator GuIndicator(ISeries<double> input )
		{
			return indicator.GuIndicator(input);
		}
	}
}

namespace NinjaTrader.NinjaScript.Strategies
{
	public partial class Strategy : NinjaTrader.Gui.NinjaScript.StrategyRenderBase
	{
		public Indicators.GuIndicator GuIndicator()
		{
			return indicator.GuIndicator(Input);
		}

		public Indicators.GuIndicator GuIndicator(ISeries<double> input )
		{
			return indicator.GuIndicator(input);
		}
	}
}

#endregion

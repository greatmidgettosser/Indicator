RiskParameters.dll / Risk_Manager_v2.cs is the version without the ability to change the daily reset time! These are hardcoded to reset at 5:00 pm eastern time during the 1 hour that the futures market is closed. The RiskManager.dll and Risk_Manager.cs version allows user to manually set the daily reset timer so if you lock the account, you can set the timer to be 1 minute from now and when that time hits the account will unlock.


Market Structure Table Custom Indicator for Quantower

Per bar expectancy has 2 values: How much price has moved relative to VWAP over time and Average point return per bar

1. The number in () is the difference in price minus VWAP over the lookback period. example: 25 bars ago price 
was 6000 and VWAP price was 6005, so difference of price to VWAP is -5 because 6000 - 6005 = -5. Current price is 6015 and current VWAP is 6008, so the difference is 7. Difference between -5 and 7 is +12 so table will show (+12) telling you how much price has moved relative to VWAP over time. + values shows BULL - values shows BEAR

2. Per Bar Expectancy is a volatility reading and simply the average point difference between open price and close price for each candlestick. If a candlestick opens at 6000 and closes at 6002.5, then its value is 2.5, high and low of the candlestick are not included. Every candlestick in the lookback period is given a value and then all values are averaged to give the average point return per candlestick. The table shows this number right next to the VWAP difference

Pivots High vs Pivots Low 
These are rolling window pivot highs and pivot lows. This means every bar is compared to 3 bars behind it and 3 bars ahead of it to determine for each candlestick if its the highest or lowest point amongst the rolling window. Closing prices are used for each candlestick. These high and low points are plotted on the chart as a small triangle above or below the candlesticks. Because it needs to look forward 3 bars it will have a delay of 3 bars before identifying new highs and lows. 3 bars ahead and behind is default but can be changed in the settings.

The pivot points are used to calculate total point return over the lookback period of all the pivot highs vs pivot lows. These pivot points represent local dips and peaks so this means its calculating the point difference 
from entering in all the dips and closing at the peaks vs entering at all the peaks and closing at the dips. The table will show either BULL or BEAR based off whether the dip entries or peak entries yielded more points over the lookback period and will show the actual points yielded such as "BEAR  up 12.25 down 16.75"

**IMPORTANT** Set it to UPDATE On Bar Close. If its set to On Tick then it will calculate the pivot points BEFORE the 3rd bar has closed and can lead to false pivot points, it does not correct false pivot points automatically. It will only correct prior false pivots when the chart is refreshed. 
Also there is no Neutral threshold, only BULL or BEAR based off positive or negative value

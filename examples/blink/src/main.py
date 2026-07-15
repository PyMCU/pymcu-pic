# PIC16F877A: Classic LED blink
# Demonstrates: Pin HAL, while loop, delay
#
# Hardware: any PIC16F877A board at 20 MHz
#   - LED on RB0
#
from pymcu.hal.gpio import Pin
from pymcu.time import delay_ms


def main():
    led = Pin("RB0", Pin.OUT)

    while True:
        led.high()
        delay_ms(100)
        led.low()
        delay_ms(100)

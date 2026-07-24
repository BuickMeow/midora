using System;
using System.Collections.Generic;
using System.Text;

namespace Midora.Midi;

public class MidoraMidiException(string message) : Exception(message);
